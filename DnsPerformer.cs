using DnsClient;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace DnsPerformer
{

    public class RootZone
    {
        private static Random rnd = new Random();
        private string rootzone;
        private string result;
        public static ConcurrentDictionary<string, List<Serveur>> ServeursDns = new ConcurrentDictionary<string, List<Serveur>>();
        private static System.Object lockThis = new System.Object();
        private static object lockObj = new object();
        public RootZone()
        {
            // download a l'initialisation de la classe.
            HttpClient client = new HttpClient();
            // nécessaire sinon 403 !
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 6.2; WOW64; rv:19.0) Gecko/20100101 Firefox/19.0");
            try
            {
                result = client.GetStringAsync("https://www.internic.net/domain/root.zone").Result;
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
            finally
            {
                rootzone = result;
            }

            MatchCollection matches = Regex.Matches(rootzone, @"^([a-z0-9\-\.]+)\.\s+.*NS\s+([a-z0-9\-\.]*)$", RegexOptions.Multiline);
            foreach (Match match in matches) // on enumère les serveurs DNS.
            {
                var extension = match.Groups[1].Value;
                var serveur = match.Groups[2].Value;
                Match match_ip = Regex.Match(rootzone, "^" + Regex.Escape(serveur) + @".*A\s+([0-9\.]+)$", RegexOptions.Multiline); // on récupère la première IP. A voir s'il existe des cas de round robin.
                var ip = match_ip.Groups[1].Value;
                AddServer(extension, new RootZone.Serveur { Queries = 0, Fails = 0, Ip = ip, Ns = serveur });
            }
        }


        public class Serveur
        {
            private int _Queries;
            private int _Fails;
            public int Queries { get { return _Queries; } set { Interlocked.Exchange(ref _Queries, value); } }
            public int Fails { get { return _Fails; } set { Interlocked.Exchange(ref _Fails, value); } }
            public string Ns { get; set; }
            public string Ip { get; set; }

            public Serveur Clone()
            {
                return new Serveur { Queries = this.Queries, Fails = this.Fails, Ip = this.Ip, Ns = this.Ns };
            }
        }

        public static void Purge()
        {
            lock (lockThis)
            {
                WriteLog("Purge...");
                for (int i = ServeursDns.Count - 1; i >= 0; i--)
                {
                    var item = ServeursDns.ElementAt(i);
                    ServeursDns[item.Key].OrderBy(server => server.Fails);
                    if (ServeursDns[item.Key].Sum(server => server.Queries) < 1) continue;

                    List<Serveur> list = ServeursDns[item.Key];

                    for (int j = list.Count - 1; j >= 0; j--)
                    {
                        // 20% de fails et au moins 100 queries.
                        if (list[j].Queries > 100 && list[j].Fails * 100 / list[j].Queries > 20)
                        {
                            var count = list.Count();
                            if (count > 1)
                            {

                                WriteLog("Le serveur " + list[j].Ns + " , " + list[j].Ip + " est purgé..");
                                list.RemoveAt(j);
                            }
                            else
                            {
                                // il ne reste qu'un seul serveur, même s'il est pourri on fait avec..
                                WriteLog("Le serveur " + list[j].Ns + " , " + list[j].Ip + " seul candidat restant.");
                            }
                        }
                    }
                }
            }
        }

        public static bool ServerExists(string extension)
        {
            if (ServeursDns.ContainsKey(extension) == true)
            {
                List<Serveur> list = ServeursDns[extension];

                return (list.Count() > 0);
            }
            else return false;
        }

        public static void AddServer(string key, Serveur value)
        {
            lock (lockThis)
            {
                if (ServeursDns.ContainsKey(key) == true)
                {
                    List<Serveur> list = ServeursDns[key];
                    if (list.Any(tmp => tmp.Ip == value.Ip) == false)
                    {
                        list.Add(value);
                    }
                }
                else
                {
                    List<Serveur> list = new List<Serveur>();
                    list.Add(value);
                    ServeursDns.TryAdd(key, list);
                }
            }
        }

        public static void GetAll()
        {
            for (int i = ServeursDns.Count - 1; i >= 0; i--)
            {
                var item = ServeursDns.ElementAt(i);
                foreach (Serveur serveur in item.Value)
                {
                    if (serveur.Queries > 0)
                        WriteLog("Extension: " + item.Key + "\tNs: " + serveur.Ns + "\tIp: " + serveur.Ip + "\tFails: " + serveur.Fails + "\tQueries: " + serveur.Queries);
                }
            }
        }

        public static void GetExtension(string extension)
        {
            var candidates = ServeursDns.Where(v => v.Key.Contains(extension));
            foreach (var candidate in candidates)
            {
                List<Serveur> value;
                if (ServeursDns.TryGetValue(candidate.Key, out value))
                {
                    foreach (Serveur serveur in value)
                    {
                        WriteLog("Extension: " + candidate.Key + "\tNs: " + serveur.Ns + "\tIp: " + serveur.Ip + "\tFails: " + serveur.Fails + "\tQueries: " + serveur.Queries);
                    }
                }
            }
        }

        public static Serveur GetServer(string extension)
        {
            List<Serveur> value;
            if (ServeursDns.TryGetValue(extension, out value))
            {
                Random localrdn = new Random();
                int r = localrdn.Next(value.Count);
                return value[r];
            }
            else
            {
                if (DnsServer.Request(extension) == true)
                    return GetServer(extension);
                else
                {
                    if (DnsServer.Request(extension.Substring(extension.IndexOf(".") + 1)) == true)
                    {
                        AddServer(extension, GetServer(extension.Substring(extension.IndexOf(".") + 1)).Clone());
                        return GetServer(extension);
                    }
                    else
                    {
                        Console.WriteLine("Impossible de recupérer des NS de {0}", extension);
                        return null;
                    }
                }
            }
        }
        public static void WriteLog(string text, ConsoleColor color = ConsoleColor.White)
        {
            lock (lockObj)
            {
                Console.ForegroundColor = color;
                ClearCurrentConsoleLine();
                Console.WriteLine(text);
                Console.ForegroundColor = ConsoleColor.White;
            }
        }

        public static void ClearCurrentConsoleLine()
        {
            int currentLineCursor = Console.CursorTop;
            Console.SetCursorPosition(0, Console.CursorTop);
            Console.Write(new string(' ', Console.WindowWidth));
            Console.SetCursorPosition(0, currentLineCursor);
        }
    }



    public static class DnsServer
    {
        private static object lockObj = new object();

        public class Data
        {
            public bool success { get; set; } = false;
            public bool result { get; set; } = false;
            public string log { get; set; } = "Defaut log value";
        }

        private static int _queryNumber;


        public static byte[] ReceiveAll(this Socket socket)
        {
            var buffer = new List<byte>();

            while (socket.Available > 0)
            {
                var currByte = new Byte[1];
                var byteCounter = socket.Receive(currByte, currByte.Length, SocketFlags.None);

                if (byteCounter.Equals(1))
                {
                    buffer.Add(currByte[0]);
                }
                else return buffer.ToArray();
            }

            return buffer.ToArray();
        }

        public static int QueryNumber
        {
            get { return _queryNumber; }
        }


        public static int Timeout = 300;
        public static int Retries = 1;

        public static string[] DnsServers
        {
            get { return _dnsEndPoints.Select(x => x.Address.ToString()).ToArray(); }
            set { _dnsEndPoints = value.Select(x => new IPEndPoint(IPAddress.Parse(x), 53)).ToArray(); }
        }


        private static IPEndPoint[] _dnsEndPoints = {
                                                    new IPEndPoint(IPAddress.Parse("8.8.8.8"), 53),
                                                    new IPEndPoint(IPAddress.Parse("8.8.4.4"), 53),
                                                    new IPEndPoint(IPAddress.Parse("1.1.1.1"), 53)
        };


        private static IPAddress[] _sortie = { 
                                                 
                                                  IPAddress.Parse("0.0.0.0"),
                                              };
        private static readonly IdnMapping Idn = new IdnMapping();


        public static bool Request(string extension)
        {
            IDnsQueryResponse result;
            var endpoint = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 53);
            var client = new LookupClient(endpoint);
            client.EnableAuditTrail = false;
            try
            {
                result = client.Query(extension, QueryType.NS);
            }
            catch (Exception e)
            {
                Console.WriteLine("ERROR\tException: " + endpoint.Address.ToString() + "\t" + e);
                return false;
            }
            foreach (var NsRecord in result.Answers.NsRecords())
            {
                var result2 = client.Query(NsRecord.NSDName, QueryType.A);
                if (result2.Answers.ARecords().Count() < 1) return false;
                var ns = NsRecord.NSDName.ToString();
                var ip = result2.Answers.ARecords()?.First()?.Address.ToString();
                if (CheckSoaServer(extension, ip))
                {
                    //Console.WriteLine("Ajout de {0} , {1}", ns, ip);
                    RootZone.Serveur serveur = new RootZone.Serveur { Queries = 0, Fails = 0, Ip = ip, Ns = ns };
                    RootZone.AddServer(extension, serveur);

                }
                //else Console.WriteLine("[DEBUG] Pas de SOA pour {0} sur {1}", extension, ip);
            }
            return RootZone.ServerExists(extension);
        }

        public static bool CheckSoaServer(string extension, string ip)
        {
            byte[] bufferReceive = new byte[512];
            byte[] finalMessage = new byte[512];
            int currentId = 0;
            int port = 0;
            int rCode = 0;
            ushort answerCount = 0;

            currentId = Interlocked.Increment(ref _queryNumber);
            var header = new byte[] { 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
            var tail = new byte[] { 0, 0, 6, 0, 1 };

            unchecked
            {
                header[0] = (byte)(currentId >> 8);
                header[1] = (byte)currentId;
            }

            var items = extension.Split('.');


            var tempMessage = header.AsEnumerable();

            foreach (var item in items)
            {
                var itemBytes = Encoding.ASCII.GetBytes(item);
                var itemLen = new[] { (byte)item.Length };
                tempMessage = tempMessage.Concat(itemLen).Concat(itemBytes);
            }

            finalMessage = tempMessage.Concat(tail).ToArray();
            port = 15000 + (currentId % 50535);


            using (var socket = new UdpClient())
            {
                socket.Client.ReceiveTimeout = 500;
                socket.ExclusiveAddressUse = false;
                socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

                var selectedBind = _sortie[currentId % _sortie.Length];
                var ipendpt = new IPEndPoint(selectedBind, 0);
                socket.Client.Bind(ipendpt);
                var selectedServer = new IPEndPoint(IPAddress.Parse(ip), 53);

                
                try
                {
                    socket.Send(finalMessage, finalMessage.Length, selectedServer);
                }
                catch (SocketException)
                {
                    socket.Close();
                    return false;
                }

                try
                {
                    bufferReceive = socket.Receive(ref ipendpt);
                }
                catch (SocketException)
                {
                    socket.Close();
                    return false;
                }
                finally
                {
                    socket.Close();
                }
                
            }
            rCode = bufferReceive[3] & 0xF;
            answerCount = BitConverter.ToUInt16(bufferReceive.Skip(6).Take(2).Reverse().ToArray(), 0);
            return (rCode == 0 && answerCount > 0);
        }

        /*
        public static async Task<Data> IsNsRecordedAsync(string domain, int timeout, string dnsserver = null)
        {
            Data output = new Data();

            byte[] bufferReceive = new byte[512];
            byte[] finalMessage = new byte[512];
            int currentId = 0;
            int port = 0;
            int rCode = 0;
            ushort answerCount = 0;
            ushort nsCount = 0;
            bool? aaFlag = null;
            int questionBytes = 0;

            DomainParser.Domain extension = DomainParser.DomainParser.Parse(domain);

            var items = domain.Split('.');
            if (extension.Extension == "") extension.Extension = items.Last();

            currentId = Interlocked.Increment(ref _queryNumber);
            if (RootZone.ServerExists(extension.Extension) && dnsserver != null)
            {
                RootZone.ServeursDns[extension.Extension].First(item => item.Ip == dnsserver).Queries++;
            }
            if (dnsserver == null)
            {
                output.log = "dnsserver is null";
                return output;
            }

            var header = new byte[] { 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
            var tail = new byte[] { 0, 0, 2, 0, 1 };
            unchecked
            {
                header[0] = (byte)(currentId >> 8);
                header[1] = (byte)currentId;
            }

            var tempMessage = header.AsEnumerable();

            foreach (var item in items)
            {
                var itemBytes = Encoding.ASCII.GetBytes(item);
                var itemLen = new[] { (byte)item.Length };
                tempMessage = tempMessage.Concat(itemLen).Concat(itemBytes);
            }

            finalMessage = tempMessage.Concat(tail).ToArray();
            port = 15000 + (currentId % 50535);


            using (var socket = new UdpClient())
            {
                socket.Client.ReceiveTimeout = 500;
                socket.ExclusiveAddressUse = true;
                socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
                var selectedBind = _sortie[currentId % _sortie.Length];
                //var ipendpt = new IPEndPoint(selectedBind, port);
                var ipendpt = new IPEndPoint(selectedBind, 0);

                var selectedServer = _dnsEndPoints[currentId % _dnsEndPoints.Length];
                if (dnsserver != null)
                {
                    DnsServer.DnsServers = new string[] { dnsserver };
                    selectedServer = new IPEndPoint(IPAddress.Parse(dnsserver), 53);
                }
                socket.Send(finalMessage, finalMessage.Length, selectedServer);
                try
                {
                    bufferReceive = socket.Receive(ref ipendpt);
                    //var tmp = await socket.ReceiveAsync().ConfigureAwait(false);
                    /*
                    var result = await Task.Run(() =>
                    {
                        var task = socket.ReceiveAsync();
                        task.Wait(timeout);
                        if (task.IsCompleted)
                        { return task.Result; }
                        throw new TimeoutException();
                    }).ConfigureAwait(false);
                    
                    bufferReceive = result.Buffer;
                   
                }
                catch (Exception e)
                {
                    output.log = "[ERREUR socket receive]\t" + dnsserver + "\t" + port + "\t" + e;
                    if (dnsserver != null) RootZone.ServeursDns[extension.Extension].First(item => item.Ip == dnsserver).Fails++;
                    return output;
                }
                finally
                {
                    socket.Close();
                }
            }

            bool areEqual = finalMessage.Take(2).ToArray().SequenceEqual(bufferReceive.Take(2).ToArray()); // true
            rCode = bufferReceive[3] & 0xF;
            aaFlag = (bufferReceive[2] & (1 << 6 - 1)) != 0;
            answerCount = BitConverter.ToUInt16(bufferReceive.Skip(6).Take(2).Reverse().ToArray(), 0);
            nsCount = BitConverter.ToUInt16(bufferReceive.Skip(8).Take(2).Reverse().ToArray(), 0);
            questionBytes = (items.Count() + domain.Length) + 4;

            //  1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0
            bool authorityPointer = bufferReceive.Skip(12 + questionBytes).Take(2).SequenceEqual(new byte[2] { (byte)192, (byte)12 });

            if (dnsserver != null)
                output.log = domain + "\tServer: " + dnsserver + "\taaFlag: " + aaFlag.ToString() + "\trCode: " + rCode + "\tanswerCount: " + Convert.ToString((int)answerCount) + "\tNSCOUNT: " + Convert.ToString((int)nsCount) + "\tPointer: " + authorityPointer.ToString() + "\t" + BitConverter.ToString(new byte[2] { (byte)192, (byte)12 }) + "\tDNS: " + BitConverter.ToString(bufferReceive.Skip(12 + questionBytes).Take(2).ToArray());
            else
                output.log = domain + "\tServer: Google\taaFlag: " + aaFlag.ToString() + "\trCode: " + rCode + "\tanswerCount: " + Convert.ToString((int)answerCount) + "\tNSCOUNT: " + Convert.ToString((int)nsCount) + "\tDNS: " + BitConverter.ToString(bufferReceive.Skip(12 + questionBytes).Take(2).ToArray());


            // Je comprend pas, ça n'arrive jamais sous windoows, je n'ai ce comportement que sous linux avec mono... ca vient du core, du serveur , de sa config réseau? 
            if (areEqual == false)
            {
                output.success = false;
                return output;
            }
            else output.success = true;


            output.result = ((rCode == 0 && (answerCount > 1 || (nsCount > 1 && dnsserver != null && authorityPointer))) == true);

            return output;
        }
        */

        public static bool IsNsRecorded(string domain, string extension, int timeout, out bool result, out string log, string dnsserver = null)
        {
            result = false;
            log = "";

            byte[] bufferReceive = new byte[512];
            byte[] finalMessage = new byte[512];
            int currentId = 0;
            int port = 0;
            int rCode = 0;
            ushort answerCount = 0;
            ushort nsCount = 0;
            bool? aaFlag = null;
            int questionBytes = 0;


            var items = domain.Split('.');

            currentId = Interlocked.Increment(ref _queryNumber);
            if (RootZone.ServerExists(extension) && dnsserver != null)
            {
                RootZone.ServeursDns[extension].First(item => item.Ip == dnsserver).Queries++;
            }
            if (dnsserver == null)
            {
                return false;
            }

            var header = new byte[] { 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
            var tail = new byte[] { 0, 0, 2, 0, 1 };

            unchecked
            {
                header[0] = (byte)(currentId >> 8);
                header[1] = (byte)currentId;
            }

            var tempMessage = header.AsEnumerable();

            foreach (var item in items)
            {
                var itemBytes = Encoding.ASCII.GetBytes(item);
                var itemLen = new[] { (byte)item.Length };
                tempMessage = tempMessage.Concat(itemLen).Concat(itemBytes);
            }

            finalMessage = tempMessage.Concat(tail).ToArray();
            port = 15000 + (currentId % 50535);


            using (var socket = new UdpClient())
            //using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                socket.Client.ReceiveTimeout = timeout;
                //socket.DontFragment = true;
                socket.ExclusiveAddressUse = true;
                socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
                //socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveTimeout, timeout);

                var selectedBind = _sortie[currentId % _sortie.Length];

                //var ipendpt = new IPEndPoint(selectedBind, 45152 + (currentId % 16380));
                var ipendpt = new IPEndPoint(selectedBind, port);
                //var ipendpt = new IPEndPoint(selectedBind, 0);
                var selectedServer = _dnsEndPoints[currentId % _dnsEndPoints.Length];
                //var ipendpt = new IPEndPoint(selectedBind, port);
                //socket.Bind(ipendpt);
                //Console.WriteLine(domain + "\t / ip : " + ipendpt.Address + " port : " + ipendpt.Port);
                if (dnsserver != null)
                {
                    DnsServer.DnsServers = new string[] { dnsserver };
                    selectedServer = new IPEndPoint(IPAddress.Parse(dnsserver), 53);
                }

                try
                {
                    socket.Send(finalMessage, finalMessage.Length, selectedServer);
                    //socket.SendTo(finalMessage, finalMessage.Length, SocketFlags.None, selectedServer);
                }
                catch (SocketException e)
                {
                    Console.WriteLine(port + "\t" + e);
                    socket.Close();
                    return false;
                }

                try
                {
                    bufferReceive = socket.Receive(ref ipendpt);
                    //bufferReceive = await socket.ReceiveFromAsync(recvargs);
                    //socket.Receive(bufferReceive);
                }
                catch (SocketException e)
                {
                    socket.Close();
                    log = "[ERROR] Exception\t" + dnsserver + "\t" + e;
                    var server = RootZone.ServeursDns[extension].FirstOrDefault(item => item.Ip == dnsserver);
                    if (dnsserver != null && server != null) server.Fails++;
                    return false;
                }
                finally
                {
                    socket.Close();
                }

            }

            //Console.WriteLine("[DEBUG] Query {0}\t\t\tusing\t{1} {2} {3}", domain, dnsserver, RootZone.ServeursDns[extension.Extension].First(item => item.Ip == dnsserver).Ip, RootZone.ServeursDns[extension.Extension].First(item => item.Ip == dnsserver).Ns);

            bool areEqual = finalMessage.Take(2).ToArray().SequenceEqual(bufferReceive.Take(2).ToArray()); // true
            rCode = bufferReceive[3] & 0xF;
            aaFlag = (bufferReceive[2] & (1 << 6 - 1)) != 0;
            answerCount = BitConverter.ToUInt16(bufferReceive.Skip(6).Take(2).Reverse().ToArray(), 0);
            nsCount = BitConverter.ToUInt16(bufferReceive.Skip(8).Take(2).Reverse().ToArray(), 0);
            questionBytes = (items.Count() + domain.Length) + 4;

            //  1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0
            bool authorityPointer = bufferReceive.Skip(12 + questionBytes).Take(2).SequenceEqual(new byte[2] { (byte)192, (byte)12 });

            if (dnsserver != null)
                log = domain + "\tServer: " + dnsserver + "\taaFlag: " + aaFlag.ToString() + "\trCode: " + rCode + "\tanswerCount: " + Convert.ToString((int)answerCount) + "\tNSCOUNT: " + Convert.ToString((int)nsCount) + "\tPointer: " + authorityPointer.ToString() + "\t" + BitConverter.ToString(new byte[2] { (byte)192, (byte)12 }) + "\tDNS: " + BitConverter.ToString(bufferReceive.Skip(12 + questionBytes).Take(2).ToArray());
            else
                log = domain + "\tServer: Google\taaFlag: " + aaFlag.ToString() + "\trCode: " + rCode + "\tanswerCount: " + Convert.ToString((int)answerCount) + "\tNSCOUNT: " + Convert.ToString((int)nsCount) + "\tDNS: " + BitConverter.ToString(bufferReceive.Skip(12 + questionBytes).Take(2).ToArray());


            /* Je comprend pas, ça n'arrive jamais sous windoows, je n'ai ce comportement que sous linux avec mono... ca vient du core, du serveur , de sa config réseau? */
            if (areEqual == false)
            {
                WriteLog("[ERROR] Not Equal !", ConsoleColor.Red);
                //Console.WriteLine(domain + "\tQ:\t" + BitConverter.ToString(finalMessage.ToArray()));
                //Console.WriteLine(port + "\t" + currentId + "\t" + domain + "\tR:\t" + BitConverter.ToString(bufferReceive.ToArray()));
                //if (dnsserver != null) RootZone.ServeursDns[extension.Extension].First(item => item.Ip == dnsserver).Fails++;
                return false;
            }

            result = false;
            if (rCode == 0 && (answerCount > 1 || (nsCount > 1 && dnsserver != null && authorityPointer))) result = true;

            return true;

        }

        public static void WriteLog(string text, ConsoleColor color = ConsoleColor.White)
        {
            lock (lockObj)
            {
                Console.ForegroundColor = color;
                ClearCurrentConsoleLine();
                Console.WriteLine(text);
                Console.ForegroundColor = ConsoleColor.White;
            }
        }

        public static void ClearCurrentConsoleLine()
        {
            int currentLineCursor = Console.CursorTop;
            Console.SetCursorPosition(0, Console.CursorTop);
            Console.Write(new string(' ', Console.WindowWidth));
            Console.SetCursorPosition(0, currentLineCursor);
        }


    }
}
