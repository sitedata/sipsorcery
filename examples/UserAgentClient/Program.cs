﻿//-----------------------------------------------------------------------------
// Filename: Program.cs
//
// Description: An abbreviated example program of how to use the SIPSorcery core library to place a SIP call.
// The example program depends on one audio input and one audio output being available.
//
// Author(s):
// Aaron Clauson  (aaron@sipsorcery.com)
// 
// History:
// 26 Oct 2019	Aaron Clauson	Created, Dublin, Ireland.
//
// License: 
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using Serilog;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Sys;

namespace SIPSorcery
{
    class Program
    {
        private static readonly string DEFAULT_DESTINATION_SIP_URI = "sip:*61@192.168.11.48";
        //private static readonly string DEFAULT_DESTINATION_SIP_URI = "sip:time@sipsorcery.com";  // Talking Clock.
        //private static readonly string DEFAULT_DESTINATION_SIP_URI = "sip:echo@sipsorcery.com"; // Echo Test.

        private static Microsoft.Extensions.Logging.ILogger Log = SIPSorcery.Sys.Log.Logger;

        private static WaveFormat _waveFormat = new WaveFormat(8000, 16, 1);  // PCMU format used by both input and output streams.
        private static int INPUT_SAMPLE_PERIOD_MILLISECONDS = 20;           // This sets the frequency of the RTP packets.
        private static IPEndPoint _remoteRtpEndPoint = null;

        static void Main()
        {
            Console.WriteLine("SIPSorcery client user agent example.");
            Console.WriteLine("Press ctrl-c to exit.");

            // Plumbing code to facilitate a graceful exit.
            ManualResetEvent exitMre = new ManualResetEvent(false);
            bool isCallHungup = false;
            bool hasCallFailed = false;

            AddConsoleLogger();

            SIPURI callUri = SIPURI.ParseSIPURI(DEFAULT_DESTINATION_SIP_URI);
            Log.LogInformation($"Call destination {callUri}.");

            // Set up a default SIP transport.
            var sipTransport = new SIPTransport();
            sipTransport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, 0)));

            EnableTraceLogs(sipTransport);

            // Get the IP address the RTP will be sent from. While we can listen on IPAddress.Any
            // we can't put 0.0.0.0 in the SDP or the callee will ignore us.
            var lookupResult = SIPDNSManager.ResolveSIPService(callUri, false);
            Log.LogDebug($"DNS lookup result for {callUri}: {lookupResult?.GetSIPEndPoint()}.");
            var dstAddress = lookupResult.GetSIPEndPoint().Address;
            IPAddress localIPAddress = NetServices.GetLocalAddressForRemote(dstAddress);

            // Initialise an RTP session to receive the RTP packets from the remote SIP server.
            RTPChannel2 rtpChannel = new RTPChannel2(IPAddress.Any, true);
            var rtpRecvSession = new RTPSession((int)RTPPayloadTypesEnum.PCMU, null, null);
            var rtpSendSession = new RTPSession((int)RTPPayloadTypesEnum.PCMU, null, null);
            rtpChannel.OnRTPDataReceived += rtpRecvSession.RtpPacketReceived;

            // Create a client user agent to place a call to a remote SIP server along with event handlers for the different stages of the call.
            var uac = new SIPClientUserAgent(sipTransport);
            uac.CallTrying += (uac, resp) =>
            {
                Log.LogInformation($"{uac.CallDescriptor.To} Trying: {resp.StatusCode} {resp.ReasonPhrase}.");
            };
            uac.CallRinging += (uac, resp) => Log.LogInformation($"{uac.CallDescriptor.To} Ringing: {resp.StatusCode} {resp.ReasonPhrase}.");
            uac.CallFailed += (uac, err) =>
            {
                Log.LogWarning($"{uac.CallDescriptor.To} Failed: {err}");
                hasCallFailed = true;
            };
            uac.CallAnswered += (uac, resp) =>
            {
                if (resp.Status == SIPResponseStatusCodesEnum.Ok)
                {
                    Log.LogInformation($"{uac.CallDescriptor.To} Answered: {resp.StatusCode} {resp.ReasonPhrase}.");

                    // Only set the remote RTP end point if there hasn't already been a packet received on it.
                    if (_remoteRtpEndPoint == null)
                    {
                        _remoteRtpEndPoint = SDP.GetSDPRTPEndPoint(resp.Body);
                        Log.LogDebug($"Remote RTP socket {_remoteRtpEndPoint}.");
                    }
                }
                else
                {
                    Log.LogWarning($"{uac.CallDescriptor.To} Answered: {resp.StatusCode} {resp.ReasonPhrase}.");
                }
            };

            // The only incoming request that needs to be explicitly handled for this example is if the remote end hangs up the call.
            sipTransport.SIPTransportRequestReceived += (SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint, SIPRequest sipRequest) =>
            {
                if (sipRequest.Method == SIPMethodsEnum.BYE)
                {
                    SIPNonInviteTransaction byeTransaction = sipTransport.CreateNonInviteTransaction(sipRequest, null);
                    SIPResponse byeResponse = SIPTransport.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Ok, null);
                    byeTransaction.SendFinalResponse(byeResponse);

                    if (uac.IsUACAnswered)
                    {
                        Log.LogInformation("Call was hungup by remote server.");
                        isCallHungup = true;
                        exitMre.Set();
                    }
                }
            };

            // Wire up the RTP receive session to the audio input device.
            var (audioOutEvent, audioOutProvider) = GetAudioOutputDevice();
            rtpRecvSession.OnSampleReady += (sample) =>
            {
                for (int index = 0; index < sample.Length; index++)
                {
                    short pcm = NAudio.Codecs.MuLawDecoder.MuLawToLinearSample(sample[index]);
                    byte[] pcmSample = new byte[] { (byte)(pcm & 0xFF), (byte)(pcm >> 8) };
                    audioOutProvider.AddSamples(pcmSample, 0, 2);
                }
            };

            // Wire up the RTP send session to the audio output device.
            WaveInEvent waveInEvent = GetAudioInputDevice();
            uint rtpSendTimestamp = 0;
            waveInEvent.DataAvailable += (object sender, WaveInEventArgs args) =>
            {
                byte[] sample = new byte[args.Buffer.Length / 2];
                int sampleIndex = 0;

                for (int index = 0; index < args.BytesRecorded; index += 2)
                {
                    var ulawByte = NAudio.Codecs.MuLawEncoder.LinearToMuLawSample(BitConverter.ToInt16(args.Buffer, index));
                    sample[sampleIndex++] = ulawByte;
                }

                if (_remoteRtpEndPoint != null)
                {
                    rtpSendSession.SendAudioFrame(rtpChannel, _remoteRtpEndPoint, rtpSendTimestamp, sample);
                    rtpSendTimestamp += (uint)(8000 / waveInEvent.BufferMilliseconds);
                }

                waveInEvent.StartRecording();
            };

            // Start the RTP and Control socket receivers.
            rtpChannel.Start();

            // Start the thread that places the call.
            SIPCallDescriptor callDescriptor = new SIPCallDescriptor(
                SIPConstants.SIP_DEFAULT_USERNAME,
                null,
                callUri.ToString(),
                SIPConstants.SIP_DEFAULT_FROMURI,
                callUri.CanonicalAddress,
                null, null, null,
                SIPCallDirection.Out,
                SDP.SDP_MIME_CONTENTTYPE,
                GetSDP(new IPEndPoint(localIPAddress, rtpChannel.RTPPort)).ToString(),
                null);

            uac.Call(callDescriptor);

            // Ctrl-c will gracefully exit the call at any point.
            Console.CancelKeyPress += delegate (object sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                exitMre.Set();
            };

            // Wait for a signal saying the call failed, was cancelled with ctrl-c or completed.
            exitMre.WaitOne();

            Log.LogInformation("Exiting...");

            waveInEvent?.StopRecording();
            audioOutEvent?.Stop();
            rtpChannel.Close();

            if (!isCallHungup && uac != null)
            {
                if (uac.IsUACAnswered)
                {
                    Log.LogInformation($"Hanging up call to {uac.CallDescriptor.To}.");
                    uac.Hangup();
                }
                else if (!hasCallFailed)
                {
                    Log.LogInformation($"Cancelling call to {uac.CallDescriptor.To}.");
                    uac.Cancel();
                }

                // Give the BYE or CANCEL request time to be transmitted.
                Log.LogInformation("Waiting 1s for call to clean up...");
                Task.Delay(1000).Wait();
            }

            SIPSorcery.Net.DNSManager.Stop();

            if (sipTransport != null)
            {
                Log.LogInformation("Shutting down SIP transport...");
                sipTransport.Shutdown();
            }
        }

        /// <summary>
        /// Get the audio output device, e.g. speaker.
        /// Note that NAUdio.Wave.WaveOut is not available for .Net Standard so no easy way to check if 
        /// there's a speaker.
        /// </summary>
        private static (WaveOutEvent, BufferedWaveProvider) GetAudioOutputDevice()
        {
            WaveOutEvent waveOutEvent = new WaveOutEvent();
            var waveProvider = new BufferedWaveProvider(_waveFormat);
            waveProvider.DiscardOnBufferOverflow = true;
            waveOutEvent.Init(waveProvider);
            waveOutEvent.Play();

            return (waveOutEvent, waveProvider);
        }

        /// <summary>
        /// Get the audio input device, e.g. microphone. The input device that will provide 
        /// audio samples that can be encoded, packaged into RTP and sent to the remote call party.
        /// </summary>
        private static WaveInEvent GetAudioInputDevice()
        {
            if (WaveInEvent.DeviceCount == 0)
            {
                throw new ApplicationException("No audio input devices available. No audio will be sent.");
            }
            else
            {
                WaveInEvent waveInEvent = new WaveInEvent();
                WaveFormat waveFormat = _waveFormat;
                waveInEvent.BufferMilliseconds = INPUT_SAMPLE_PERIOD_MILLISECONDS; 
                waveInEvent.NumberOfBuffers = 1;
                waveInEvent.DeviceNumber = 0;
                waveInEvent.WaveFormat = waveFormat;

                return waveInEvent;
            }
        }

        /// <summary>
        /// Gets the Session Description Protocol object for the initial INVITE request.
        /// </summary>
        /// <param name="rtpSocket">The RTP socket we will be sending from. Note this can't be IPAddress.Any as
        /// it's getting sent to the callee. An IP address of 0.0.0.0 or [::0] will typically be interpreted as
        /// "don't send me any RTP".</param>
        /// <returns>An Session Description Protocol object that can be sent to a remote callee.</returns>
        private static SDP GetSDP(IPEndPoint rtpSocket)
        {
            var sdp = new SDP()
            {
                SessionId = Crypto.GetRandomInt(5).ToString(),
                Address = rtpSocket.Address.ToString(),
                SessionName = "sipsorcery",
                Timing = "0 0",
                Connection = new SDPConnectionInformation(rtpSocket.Address.ToString()),
            };

            var audioAnnouncement = new SDPMediaAnnouncement()
            {
                Media = SDPMediaTypesEnum.audio,
                MediaFormats = new List<SDPMediaFormat>() { new SDPMediaFormat((int)SDPMediaFormatsEnum.PCMU, "PCMU", 8000) }
            };
            audioAnnouncement.Port = rtpSocket.Port;
            audioAnnouncement.MediaStreamStatus = MediaStreamStatusEnum.SendRecv;
            sdp.Media.Add(audioAnnouncement);

            return sdp;
        }

        /// <summary>
        ///  Adds a console logger. Can be ommitted if internal SIPSorcery debug and warning messages are not required.
        /// </summary>
        private static void AddConsoleLogger()
        {
            var loggerFactory = new Microsoft.Extensions.Logging.LoggerFactory();
            var loggerConfig = new LoggerConfiguration()
                .Enrich.FromLogContext()
                .MinimumLevel.Is(Serilog.Events.LogEventLevel.Debug)
                .WriteTo.Console()
                .CreateLogger();
            loggerFactory.AddSerilog(loggerConfig);
            SIPSorcery.Sys.Log.LoggerFactory = loggerFactory;
        }

        /// <summary>
        /// Enable detailed SIP log messages.
        /// </summary>
        private static void EnableTraceLogs(SIPTransport sipTransport)
        {
            sipTransport.SIPRequestInTraceEvent += (localEP, remoteEP, req) =>
            {
                Log.LogDebug($"Request received: {localEP}<-{remoteEP}");
                Log.LogDebug(req.ToString());
            };

            sipTransport.SIPRequestOutTraceEvent += (localEP, remoteEP, req) =>
            {
                Log.LogDebug($"Request sent: {localEP}->{remoteEP}");
                Log.LogDebug(req.ToString());
            };

            sipTransport.SIPResponseInTraceEvent += (localEP, remoteEP, resp) =>
            {
                Log.LogDebug($"Response received: {localEP}<-{remoteEP}");
                Log.LogDebug(resp.ToString());
            };

            sipTransport.SIPResponseOutTraceEvent += (localEP, remoteEP, resp) =>
            {
                Log.LogDebug($"Response sent: {localEP}->{remoteEP}");
                Log.LogDebug(resp.ToString());
            };

            sipTransport.SIPRequestRetransmitTraceEvent += (tx, req, count) =>
            {
                Log.LogDebug($"Request retransmit {count} for request {req.StatusLine}, initial transmit {DateTime.Now.Subtract(tx.InitialTransmit).TotalSeconds.ToString("0.###")}s ago.");
            };

            sipTransport.SIPResponseRetransmitTraceEvent += (tx, resp, count) =>
            {
                Log.LogDebug($"Response retransmit {count} for response {resp.ShortDescription}, initial transmit {DateTime.Now.Subtract(tx.InitialTransmit).TotalSeconds.ToString("0.###")}s ago.");
            };
        }
    }
}
