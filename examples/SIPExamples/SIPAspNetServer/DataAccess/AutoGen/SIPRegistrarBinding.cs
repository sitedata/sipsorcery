﻿using System;
using System.Collections.Generic;

#nullable disable

namespace SIPAspNetServer.DataAccess
{
    public partial class SIPRegistrarBinding
    {
        public Guid ID { get; set; }
        public Guid SIPAccountID { get; set; }
        public string UserAgent { get; set; }
        public string ContactURI { get; set; }
        public string MangledContactURI { get; set; }
        public int Expiry { get; set; }
        public string RemoteSIPSocket { get; set; }
        public string ProxySIPSocket { get; set; }
        public string RegistrarSIPSocket { get; set; }
        public DateTimeOffset LastUpdate { get; set; }
        public DateTimeOffset ExpiryTime { get; set; }

        public virtual SIPAccount SIPAccount { get; set; }
    }
}