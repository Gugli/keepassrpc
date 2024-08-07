﻿using Jayrock.Json.Conversion;
using KeePassLib.Utility;
using KeePassRPC.Models.DataExchange;

namespace KeePassRPC
{
    internal class KeyChallengeResponse
    {
        public string cc;
        public string cr;
        public string sc;
        public string sr;
        private static int ProtocolVersion;
        private string[] features;

        public KeyChallengeResponse (int protocolVersion, string[] features)
        {
            ProtocolVersion = protocolVersion;
            this.features = features;
        }

        public string KeyChallengeResponse1(string userName, int securityLevel)
        {
            BigInteger scTemp = new BigInteger(Utils.GetRandomBytes(32));
            sc = scTemp.ToString().ToLower();

            KPRPCMessage data2client = new KPRPCMessage();
            data2client.protocol = "setup";
            data2client.key = new KeyParams();
            data2client.key.sc = sc;
            data2client.key.securityLevel = securityLevel;
            data2client.version = ProtocolVersion;
            data2client.features = features;

            string response = JsonConvert.ExportToString(data2client);
            return response;
        }

        public string KeyChallengeResponse2(string cc, string cr, KeyContainerClass kc, int securityLevel, out bool authorised)
        {
            string response = null;
            this.cc = cc;
            this.cr = MemUtil.ByteArrayToHexString(Utils.Hash("1" + kc.Key + sc + this.cc)).ToLower();
            if (cr != this.cr)
            {
                authorised = false;
                KPRPCMessage data2client = new KPRPCMessage();
                data2client.protocol = "setup";
                data2client.version = ProtocolVersion;
                data2client.error = new Error(ErrorCode.AUTH_FAILED, new[] { "Keys do not match" });
                response = JsonConvert.ExportToString(data2client);
            }
            else
            {
                sr = MemUtil.ByteArrayToHexString(Utils.Hash("0" + kc.Key + sc + this.cc)).ToLower();
                authorised = true;

                KPRPCMessage data2client = new KPRPCMessage();
                data2client.protocol = "setup";
                data2client.key = new KeyParams();
                data2client.key.sr = sr;
                data2client.key.securityLevel = securityLevel;
                data2client.version = ProtocolVersion;
                response = JsonConvert.ExportToString(data2client);
            }
            return response;
        }

    }
}
