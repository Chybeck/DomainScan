using DnsPerformer;
using DomainDb;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

internal class DomainScan
{
    private static bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private static string _mySqlServer = "chybeck.net";
    private static string _mySqlUsername = "root";
    private static string _mySqlPassword = ""; // prod je l'enlève pour giter le projet, il faudra lancer le programme avec le paramètre -mp
    private static string _mySqlDatabase = "domains";

    private static int _taskCount = 512;
    private static int _processBuffer = 10000;
    private static bool _hardcore = true;
    private static int _timeout = 2000;
    private static int _retries = 1;
    private static string _wksName = Environment.MachineName;

    
    private static RRQueue.RRQueue stats = new RRQueue.RRQueue(60);
    private static string _query = "impots.gouv.fr";
    private static readonly IdnMapping Idn = new IdnMapping();
    private static bool IsFileOlder(string fileName, TimeSpan thresholdAge)
    {
        return (DateTime.Now - File.GetCreationTime(fileName)) > thresholdAge;
    }
    private static object lockObj = new object();

    private static int _failed;
    private static int _found;
    private static int _inserted;
    private static int _libelles;


    /* Main */
    private static void Main(string[] args)
    {

        ParseArgs(args);
        Console.SetWindowSize(100, 30);
        //Console.SetCursorPosition(0, 5);

        Console.BackgroundColor = ConsoleColor.Blue;
        Console.ForegroundColor = ConsoleColor.White;
        ConsoleKeyInfo cki;
        Console.WriteLine("Starting DnsTest with workstation name {0}", _wksName);
        Console.WriteLine("Touches autorisées: S pour search, G pour lister les serveurs, P pour purger les serveurs");
        Console.WriteLine("Mode Hardcore {0}", _hardcore.ToString());

        var rootzone = new RootZone(); // on charge la zone.root de l'iana.
        Check4PublicSuffixList();
        DnsServer.Retries = _retries;
        DnsServer.Timeout = _timeout;


        // debuguer.
        /*
        bool result;
        string log;
        var domain = DomainParser.DomainParser.Parse("fzbiuzhfizufizfzfz.fr");
        Console.WriteLine("{0} , {1} , {2}", domain.DomainName, domain.Libelle, domain.Extension);
        var DnsServer = RootZone.GetServer(domain.DomainName)?.Ip;
        RootZone.GetAll();
        DnsServer.IsNsRecorded(domain.DomainName, 2000, out result, out log, DnsServer);
        Console.WriteLine(log);



        var domain2 = DomainParser.DomainParser.Parse("maktalent.xn--kprw13d");
        Console.WriteLine("{0} , {1} , {2}", domain2.DomainName, domain2.Libelle, domain2.Extension);
        var DnsServer2 = RootZone.GetServer("xn--kprw13d")?.Ip;
        RootZone.GetAll();
        DnsServer.IsNsRecorded("maktalent.xn--kprw13d", 2000, out result, out log, DnsServer2);
        Console.WriteLine(log);

        Console.ReadKey();
        */

        var doneEvent = new AutoResetEvent(false);

        var taskPool = new TaskPool(_taskCount + 1);
        var dbDomains = new List<DomainDb.Domain>();
        Full_List_Extensions = Full_List_Extensions.Except(List_Extensions).ToArray();


        DomainDb.Db.ConnectionString = string.Format("Server={0};Database={1};Uid={2};Pwd={3};Port={4};MinimumPoolSize=16;maximumpoolsize=64;Charset=utf8;", _mySqlServer, _mySqlDatabase, _mySqlUsername, _mySqlPassword, 3306, _taskCount);
        Console.WriteLine("Init thread pool...");

        var queueDomainPack = new Queue<List<DomainDb.Domain>>();
        var sw = new Stopwatch();
        Stopwatch swUpdate = new Stopwatch();

        Task.Factory.StartNew(() =>
        {

            while (true)
            {
                lock (lockObj)
                {
                    RootZone.Purge();
                }

                if (sw.IsRunning) sw.Stop();
                if (swUpdate.IsRunning) swUpdate.Stop();
                Console.WriteLine("Scanning...");

                using (var db = new Db())
                {
                    Console.WriteLine("Reset libelle...");
                    db.ResetLibelle(_wksName);
                    Thread.Sleep(1000);
                    Console.WriteLine("Get For Processing...");
                    queueDomainPack.Enqueue(db.GetForProcessing(_wksName, _processBuffer));
                }

                Console.WriteLine("Dequeue...");
                dbDomains.Clear();
                dbDomains = queueDomainPack.Dequeue();

                Console.WriteLine("Querying {0} entries...", dbDomains.Count);

                if (!sw.IsRunning)
                    sw.Start();
                if (!swUpdate.IsRunning)
                    swUpdate.Start();

                for (int i = 0; i < dbDomains.Count; i++)
                {
                    var i1 = i;
                    //taskPool.QueueTask(async () =>
                    taskPool.QueueTask(() =>
                    {

                        Interlocked.Increment(ref _libelles);
                        bool insertion = false;
                        Random rnd = new Random();

                        foreach (string List_Extension in List_Extensions.OrderBy(x => rnd.Next()).ToArray())
                        {
                            var domainEntity = dbDomains[i1];
                            bool result;
                            string log;

                            domainEntity.DomainName = string.Concat(domainEntity.Libelle, List_Extension);
                            //domainEntity.Extension = DomainParser.DomainParser.Parse(domainEntity.DomainName).Extension;
                            domainEntity.Extension = List_Extension.Substring(1);
                            var DnsServer = RootZone.GetServer(domainEntity.Extension)?.Ip;

                            int thistimeout = _timeout;

                            if (List_Extension == ".cn") thistimeout = 2000;
                            if (List_Extension == ".cc") thistimeout = 2000;
                            if (List_Extension == ".ca") thistimeout = 1000;
                            if (List_Extension == ".co") thistimeout = 500;

                            var thread = Thread.CurrentThread.ManagedThreadId;
                            //var NsRecorded = await DnsServer.IsNsRecordedAsync(domainEntity.DomainName, thistimeout, DnsServer);


                            //if (!NsRecorded.success)
                            if (!DnsPerformer.DnsServer.IsNsRecorded(domainEntity.DomainName, domainEntity.Extension, thistimeout, out result, out log, DnsServer))
                            {
                                Interlocked.Increment(ref _failed);
                                domainEntity.Processing = null;
                            }
                            else
                            {

                                domainEntity.Processing = null;

                                if (isWindows) domainEntity.LastDate = TimeZoneInfo.ConvertTime(DateTime.Now, TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time"));
                                else domainEntity.LastDate = TimeZoneInfo.ConvertTime(DateTime.Now, TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris"));
                                domainEntity.NsRecorded = result;
                                //domainEntity.NsRecorded = NsRecorded.result;


                                if (domainEntity.NsRecorded == true)
                                {
                                    if (_hardcore == true) insertion = true;
                                    Interlocked.Increment(ref _found);
                                    using (var db = new DomainDb.Db())
                                    {
                                        if (db.Save(domainEntity))
                                        {
                                            if (_hardcore == false) insertion = true;
                                            Interlocked.Increment(ref _inserted);
                                        }
                                    }
                                }
                            }
                        } // foreach


                        // repasse sur l'ensemble si première fois ou si on a detecté un nom dans la première liste.
                        if (insertion == true || dbDomains[i1].LastDate == null)
                        {
                            //Console.WriteLine("[DEBUG] Passe complète sur " + dbDomains[i1].Libelle);
                            foreach (string List_Extension in Full_List_Extensions.OrderBy(x => rnd.Next()).ToArray())
                            {

                                var domainEntity = dbDomains[i1];
                                bool result;
                                string log;


                                domainEntity.DomainName = string.Concat(domainEntity.Libelle, List_Extension);
                                domainEntity.Extension = List_Extension.Substring(1);
                                var DnsServer = RootZone.GetServer(domainEntity.Extension)?.Ip;

                                int thistimeout = _timeout;

                                if (List_Extension == ".cn") thistimeout = 2000;
                                if (List_Extension == ".cc") thistimeout = 2000;
                                if (List_Extension == ".ca") thistimeout = 500;
                                if (List_Extension == ".co") thistimeout = 500;


                                var thread = Thread.CurrentThread.ManagedThreadId;

                                if (!DnsPerformer.DnsServer.IsNsRecorded(domainEntity.DomainName, domainEntity.Extension, thistimeout, out result, out log, DnsServer))
                                {
                                    Interlocked.Increment(ref _failed);
                                    domainEntity.Processing = null;
                                }
                                else
                                {

                                    domainEntity.Processing = null;
                                    if (isWindows) domainEntity.LastDate = TimeZoneInfo.ConvertTime(DateTime.Now, TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time"));
                                    else domainEntity.LastDate = TimeZoneInfo.ConvertTime(DateTime.Now, TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris"));
                                    domainEntity.NsRecorded = result;

                                    if (domainEntity.NsRecorded == true)
                                    {

                                        using (var db = new Db())
                                        {
                                            Interlocked.Increment(ref _found);
                                            if (db.Save(domainEntity))
                                            {
                                                Interlocked.Increment(ref _inserted);
                                                insertion = true;
                                            }
                                        }
                                    }
                                }

                            } // foreach
                        }
                    }); //taskpool
                } // for buffer dbDomains
                taskPool.WaitAll();
                dbDomains.Clear();
            } //while true
        }
        , TaskCreationOptions.LongRunning);
        taskPool.WaitAll();

        Task.Factory.StartNew(() =>
        {
            int precedentQuery = 0;
            swUpdate.Restart();
            while (true)
            {
                if (DnsServer.QueryNumber > 0 && dbDomains.Count > 0)
                {
                    int maxworkerThreads;
                    int maxportThreads;
                    int availableworkerThreads;
                    int availableportThreads;
                    ThreadPool.GetMaxThreads(out maxworkerThreads, out maxportThreads);
                    ThreadPool.GetAvailableThreads(out availableworkerThreads, out availableportThreads);

                    var currentQuery = DnsServer.QueryNumber;
                    stats.Enqueue((currentQuery - precedentQuery) / swUpdate.Elapsed.TotalSeconds);
                    precedentQuery = currentQuery;
                    swUpdate.Restart();
                    lock (lockObj)
                    {
                        // ouais ok y'avait plus propre.
                        var console = Console.CursorTop;
                        Console.ResetColor();
                        Console.SetCursorPosition(0, console);
                        Console.Write(new string(' ', Console.WindowWidth));
                        Console.SetCursorPosition(0, console + 1);
                        Console.Write(new string(' ', Console.WindowWidth));
                        Console.SetCursorPosition(0, console + 2);
                        Console.Write(new string(' ', Console.WindowWidth));
                        Console.BackgroundColor = ConsoleColor.DarkGreen;
                        Console.ForegroundColor = ConsoleColor.White;
                        Console.SetCursorPosition(0, console + 3);
                        Console.Write("{0}/{1} threads en cours  /  {2:0.000} QPS instantané / {3:0.000} QPS total /  {4}/{5} libelles tested\n", maxworkerThreads - availableworkerThreads - 1, taskPool.GetTasks(), stats.Average(), DnsServer.QueryNumber / sw.Elapsed.TotalSeconds, _libelles, dbDomains.Count);
                        Console.Write("{0} Domains tested  /  {1}% Fail  /  {2} Domains Found  /  {3} New Inserted\n", DnsServer.QueryNumber, (_failed / (float)DnsServer.QueryNumber) * 100, _found, _inserted);
                            Console.SetCursorPosition(0, console);
                        Console.BackgroundColor = ConsoleColor.Blue;
                        Console.ForegroundColor = ConsoleColor.White;

                    }
                }

                Thread.Sleep(1000);
            }
        });


        do
        {
            cki = Console.ReadKey();
            Console.TreatControlCAsInput = true;
            if (cki.Key == ConsoleKey.G)
            {
                lock (lockObj)
                {
                    RootZone.GetAll();
                }
                continue;
            }
            if (cki.Key == ConsoleKey.P)
            {
                lock (lockObj)
                {
                    RootZone.Purge();
                }
                continue;
            }
            if (cki.Key == ConsoleKey.S)
            {
                WriteLog("Extension à rechercher : ");
                lock (lockObj)
                {
                    var extension = Console.ReadLine();
                    RootZone.GetExtension(extension);
                }
                continue;
            }
            else
            {
                WriteLog("Touches autorisées: S pour search, G pour lister les serveurs, P pour purger les serveurs");
            }
        } while (cki.Key != ConsoleKey.Escape);


        sw.Stop();

        Console.WriteLine();
        Console.ReadKey();

    } // Main



    private static void Check4PublicSuffixList()
    {
        bool oldEnough = false;
        if (File.Exists(@"public_suffix_list.dat") == true)
            oldEnough = IsFileOlder(@"public_suffix_list.dat", new TimeSpan(7, 0, 0, 0));
        else
        {
            oldEnough = true;
        }
        if (oldEnough == true)
        {
            ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
			ServicePointManager.ServerCertificateValidationCallback += (p1, p2, p3, p4) => true;
            Console.WriteLine("Téléchargement de public_suffix_list.dat");
            using (var version = new WebClient())
                try
                {
                    version.DownloadFile("https://publicsuffix.org/list/public_suffix_list.dat", @"public_suffix_list.dat");
                }
                catch (Exception e)
                {
                    WriteLog(e.ToString());
                }
        }
    }
    private static void ParseArgs(string[] args)
    {
        for (int index = 0; index < args.Length; index++)
        {
            var arg = args[index];

            switch (arg)
            {
                case "-query":
                    _query = args[index + 1];
                    break;
                case "-taskcount":
                    _taskCount = int.Parse(args[index + 1]);
                    break;
                case "-processbuffer":
                    _processBuffer = int.Parse(args[index + 1]);
                    break;
                case "-timeout":
                    _timeout = int.Parse(args[index + 1]);
                    break;
                case "-retries":
                    _retries = int.Parse(args[index + 1]);
                    break;
                case "-mh":
                    _mySqlServer = args[index + 1];
                    break;
                case "-mu":
                    _mySqlUsername = args[index + 1];
                    break;
                case "-mp":
                    _mySqlPassword = args[index + 1];
                    break;
                case "-md":
                    _mySqlDatabase = args[index + 1];
                    break;
                case "-wksname":
                    _wksName = args[index + 1];
                    break;
                case "-hardcore":
                    _hardcore = bool.Parse(args[index + 1]);
                    break;
            }
        }
    }
    private static string ConvertToUnicode(string domain)
    {
        string _idn = string.Empty;
        try
        {
            _idn = Idn.GetUnicode(domain);
        }
        catch (Exception e)
        {
            WriteLog("ERRORPUNNY\t" + domain + "\t" + e, ConsoleColor.Magenta);
            return domain.ToLower();
        }
        return _idn.ToLower();
    }
    private static string ConvertToPunnycode(string domain)
    {
        string _punyCode = string.Empty;
        try
        {
            _punyCode = Idn.GetAscii(domain);
        }
        catch (Exception e)
        {
            WriteLog("ERRORPUNNY\t" + domain + "\t" + e, ConsoleColor.Magenta);
            return domain.ToLower();
        }
        return _punyCode.ToLower();
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


    public class TaskPool
    {
        public int TaskCount { get; private set; }

        private readonly Task[] _tasks;

        public TaskPool(int taskCount)
        {
            TaskCount = taskCount;
            _tasks = new Task[TaskCount];
            for (var i = 0; i < TaskCount; i++)
            {
                _tasks[i] = Task.Factory.StartNew(() => 0, TaskCreationOptions.LongRunning);
            }
        }

        public void QueueTask(Action action)
        {
            var index = Task.WaitAny(_tasks);
            _tasks[index] = _tasks[index].ContinueWith(task => action());
        }



        public void WaitAll()
        {
            Task.WaitAll(_tasks);
        }

        public int GetTasks()
        {
            return _tasks.Length;
        }
    }


    // les 50 extensions principales qui vont servir de trigger
    public static string[] List_Extensions =
    {
            ".ar",
            ".au",
            ".at",
            ".be",
            ".ca",
            ".ch",
            ".cl",
            ".cn",
            ".co",
            ".co.in",
            ".co.kr",
            ".co.za",
            ".com.ar",
            ".com.au",
            ".com.pl",
            ".com.pt",
            ".cz",
            ".de",
            ".es",
            ".eu",
            ".fi",
            ".fr",
            ".gr",
            ".hu",
            ".ie",
            ".il",
            ".in",
            ".io",
            ".ir",
            ".it",
            ".jp",
            ".kz",
            ".la",
            ".ma",
            ".me",
            ".mx",
            ".my",
            ".nl",
            ".no",
            ".nz",
            ".pl",
            ".pt",
            ".ro",
            ".rs",
            ".sg",
            ".sk",
            ".tk",
            ".tr",
            ".tv",
            ".tw",
            ".ua",
        };


    public static string[] Full_List_Extensions =
    {
            ".ac",
            ".ac.cn",
            ".ac.uk",
            ".ad",
            ".ae",
            ".af",
            ".ag",
            ".ah.cn",
            ".ai",
            ".al",
            ".am",
            ".ao",
            ".aq",
            ".as",
            ".asn.au",
            ".asso.fr",
            ".aw",
            ".ax",
            ".az",
            ".ba",
            ".bb",
            ".bc.ca",
            ".bf",
            ".bg",
            ".bh",
            ".bi",
            ".biz.az",
            ".biz.bb",
            ".biz.cy",
            ".biz.et",
            ".biz.id",
            ".biz.ki",
            ".biz.mv",
            ".biz.mw",
            ".biz.ni",
            ".biz.nr",
            ".biz.pk",
            ".biz.pl",
            ".biz.pr",
            ".biz.tj",
            ".biz.tr",
            ".biz.tt",
            ".biz.ua",
            ".biz.vn",
            ".biz.zm",
            ".bj",
            ".bj.cn",
            ".bm",
            ".bn",
            ".bo",
            ".br",
            ".bs",
            ".bt",
            ".bv",
            ".bw",
            ".by",
            ".bz",
            ".cc",
            ".cd",
            ".cf",
            ".cg",
            ".ci",
            ".cm",
            ".co.ae",
            ".co.ag",
            ".co.at",
            ".co.ao",
            ".co.bb",
            ".co.bi",
            ".co.bw",
            ".co.ca",
            ".co.ci",
            ".co.cl",
            ".co.com",
            ".co.cr",
            ".co.cz",
            ".co.gg",
            ".co.gl",
            ".co.gy",
            ".co.id",
            ".co.il",
            ".co.im",
            ".co.ir",
            ".co.it",
            ".co.je",
            ".co.jp",
            ".co.ke",
            ".co.lc",
            ".co.ls",
            ".co.ma",
            ".co.me",
            ".co.mu",
            ".co.mw",
            ".co.na",
            ".co.nl",
            ".co.no",
            ".co.nz",
            ".co.om",
            ".co.pl",
            ".co.pn",
            ".co.rs",
            ".co.rw",
            ".co.sz",
            ".co.th",
            ".co.tj",
            ".co.tm",
            ".co.tt",
            ".co.tz",
            ".co.ua",
            ".co.ug",
            ".co.us",
            ".co.uz",
            ".co.ve",
            ".co.vi",
            ".co.zm",
            ".com.ac",
            ".com.af",
            ".com.ag",
            ".com.ai",
            ".com.al",
            ".com.aw",
            ".com.az",
            ".com.ba",
            ".com.bb",
            ".com.bh",
            ".com.bi",
            ".com.bm",
            ".com.bo",
            ".com.br",
            ".com.bs",
            ".com.bt",
            ".com.by",
            ".com.bz",
            ".com.ci",
            ".com.cn",
            ".com.co",
            ".com.cu",
            ".com.cw",
            ".com.cy",
            ".com.de",
            ".com.dm",
            ".com.do",
            ".com.dz",
            ".com.ec",
            ".com.ee",
            ".com.eg",
            ".com.es",
            ".com.et",
            ".com.fr",
            ".com.ge",
            ".com.gh",
            ".com.gi",
            ".com.gl",
            ".com.gn",
            ".com.gp",
            ".com.gr",
            ".com.gt",
            ".com.gy",
            ".com.hk",
            ".com.hn",
            ".com.hr",
            ".com.ht",
            ".com.im",
            ".com.io",
            ".com.iq",
            ".com.is",
            ".com.jo",
            ".com.kg",
            ".com.ki",
            ".com.km",
            ".com.kp",
            ".com.ky",
            ".com.kz",
            ".com.la",
            ".com.lb",
            ".com.lc",
            ".com.lk",
            ".com.lr",
            ".com.lv",
            ".com.ly",
            ".com.mg",
            ".com.mk",
            ".com.ml",
            ".com.mo",
            ".com.ms",
            ".com.mt",
            ".com.mu",
            ".com.mv",
            ".com.mw",
            ".com.mx",
            ".com.my",
            ".com.na",
            ".com.nf",
            ".com.ng",
            ".com.nr",
            ".com.om",
            ".com.pa",
            ".com.pe",
            ".com.pf",
            ".com.pg",
            ".com.ph",
            ".com.pk",
            ".com.pr",
            ".com.ps",
            ".com.py",
            ".com.qa",
            ".com.re",
            ".com.ro",
            ".com.ru",
            ".com.rw",
            ".com.sa",
            ".com.sb",
            ".com.sc",
            ".com.sd",
            ".com.se",
            ".com.sg",
            ".com.sh",
            ".com.sl",
            ".com.sn",
            ".com.so",
            ".com.sv",
            ".com.sy",
            ".com.tj",
            ".com.tm",
            ".com.tn",
            ".com.to",
            ".com.tr",
            ".com.tt",
            ".com.tw",
            ".com.ua",
            ".com.ug",
            ".com.uy",
            ".com.uz",
            ".com.vc",
            ".com.ve",
            ".com.vi",
            ".com.vn",
            ".com.vu",
            ".com.ws",
            ".com.zm",
            ".cq.cn",
            ".cr",
            ".cu",
            ".cv",
            ".cw",
            ".cx",
            ".dj",
            ".dm",
            ".do",
            ".dp.ua",
            ".dz",
            ".ebiz.tw",
            ".ec",
            ".eco.br",
            ".edu",
            ".edu.co",
            ".edu.kz",
            ".edu.mo",
            ".edu.pk",
            ".ee",
            ".eg",
            ".er",
            ".et",
            ".firm.in",
            ".fj",
            ".fj.cn",
            ".fk",
            ".fm",
            ".fo",
            ".ga",
            ".gb",
            ".gd",
            ".gd.cn",
            ".ge",
            ".gen.in",
            ".gen.tr",
            ".gf",
            ".gg",
            ".gh",
            ".gi",
            ".gl",
            ".gm",
            ".gn",
            ".go.ug",
            ".gov",
            ".gov.au",
            ".gov.hk",
            ".gov.ph",
            ".gov.pk",
            ".gov.rw",
            ".gov.uk",
            ".gov.vn",
            ".gp",
            ".gq",
            ".gr.jp",
            ".gs",
            ".gs.cn",
            ".gt",
            ".gu",
            ".gw",
            ".gx.cn",
            ".gy",
            ".gz.cn",
            ".ha.cn",
            ".hb.cn",
            ".he.cn",
            ".hi.cn",
            ".hk",
            ".hk.cn",
            ".hl.cn",
            ".hm",
            ".hn",
            ".hn.cn",
            ".hr",
            ".ht",
            ".id",
            ".id.au",
            ".im",
            ".in.th",
            ".ind.br",
            ".ind.in",
            ".info.nr",
            ".info.pl",
            ".info.ve",
            ".int",
            ".iq",
            ".is",
            ".je",
            ".jl.cn",
            ".jm",
            ".jo",
            ".js.cn",
            ".jx.cn",
            ".kg",
            ".kh",
            ".ki",
            ".kiwi.nz",
            ".km",
            ".kn",
            ".kp",
            ".kr",
            ".kw",
            ".ky",
            ".lb",
            ".lc",
            ".lecco.it",
            ".li",
            ".lk",
            ".ln.cn",
            ".lr",
            ".ls",
            ".lt",
            ".lu",
            ".lv",
            ".ly",
            ".mc",
            ".md",
            ".med.br",
            ".med.pl",
            ".mg",
            ".mh",
            ".mil",
            ".mk",
            ".ml",
            ".mm",
            ".mn",
            ".mo",
            ".mo.cn",
            ".mp",
            ".mq",
            ".mr",
            ".ms",
            ".msk.ru",
            ".mt",
            ".mu",
            ".mv",
            ".mw",
            ".mz",
            ".na",
            ".nc",
            ".ne",
            ".ne.jp",
            ".ne.kr",
            ".net.ac",
            ".net.ae",
            ".net.af",
            ".net.ag",
            ".net.ai",
            ".net.al",
            ".net.ar",
            ".net.au",
            ".net.az",
            ".net.ba",
            ".net.bb",
            ".net.bh",
            ".net.bm",
            ".net.bo",
            ".net.br",
            ".net.bs",
            ".net.bt",
            ".net.bz",
            ".net.ci",
            ".net.cn",
            ".net.co",
            ".net.cu",
            ".net.cw",
            ".net.cy",
            ".net.dm",
            ".net.do",
            ".net.dz",
            ".net.ec",
            ".net.eg",
            ".net.et",
            ".net.ge",
            ".net.gg",
            ".net.gn",
            ".net.gp",
            ".net.gr",
            ".net.gt",
            ".net.gy",
            ".net.hk",
            ".net.hn",
            ".net.ht",
            ".net.id",
            ".net.il",
            ".net.im",
            ".net.in",
            ".net.iq",
            ".net.ir",
            ".net.is",
            ".net.je",
            ".net.jo",
            ".net.kg",
            ".net.ki",
            ".net.kn",
            ".net.ky",
            ".net.kz",
            ".net.la",
            ".net.lb",
            ".net.lc",
            ".net.lk",
            ".net.lr",
            ".net.lv",
            ".net.ly",
            ".net.ma",
            ".net.me",
            ".net.mk",
            ".net.ml",
            ".net.mo",
            ".net.ms",
            ".net.mt",
            ".net.mu",
            ".net.mv",
            ".net.mw",
            ".net.mx",
            ".net.my",
            ".net.nf",
            ".net.ng",
            ".net.nr",
            ".net.nz",
            ".net.om",
            ".net.pa",
            ".net.pe",
            ".net.ph",
            ".net.pk",
            ".net.pl",
            ".net.pn",
            ".net.pr",
            ".net.ps",
            ".net.pt",
            ".net.py",
            ".net.qa",
            ".net.ru",
            ".net.rw",
            ".net.sa",
            ".net.sb",
            ".net.sc",
            ".net.sd",
            ".net.sg",
            ".net.sh",
            ".net.sl",
            ".net.so",
            ".net.st",
            ".net.sy",
            ".net.th",
            ".net.tj",
            ".net.tm",
            ".net.tn",
            ".net.to",
            ".net.tr",
            ".net.tt",
            ".net.tw",
            ".net.ua",
            ".net.uy",
            ".net.uz",
            ".net.vc",
            ".net.ve",
            ".net.vi",
            ".net.vn",
            ".net.vu",
            ".net.ws",
            ".net.za",
            ".net.zm",
            ".nf",
            ".ng",
            ".nm.cn",
            ".nom.es",
            ".np",
            ".nr",
            ".nx.cn",
            ".od.ua",
            ".om",
            ".or.id",
            ".or.jp",
            ".or.kr",
            ".org.ac",
            ".org.ae",
            ".org.af",
            ".org.ag",
            ".org.ai",
            ".org.al",
            ".org.ar",
            ".org.au",
            ".org.az",
            ".org.ba",
            ".org.bb",
            ".org.bh",
            ".org.bi",
            ".org.bm",
            ".org.bo",
            ".org.br",
            ".org.bs",
            ".org.bt",
            ".org.bw",
            ".org.bz",
            ".org.ci",
            ".org.cn",
            ".org.co",
            ".org.cu",
            ".org.cw",
            ".org.cy",
            ".org.dm",
            ".org.do",
            ".org.dz",
            ".org.ec",
            ".org.ee",
            ".org.eg",
            ".org.es",
            ".org.et",
            ".org.ge",
            ".org.gg",
            ".org.gh",
            ".org.gi",
            ".org.gl",
            ".org.gn",
            ".org.gp",
            ".org.gr",
            ".org.gt",
            ".org.gy",
            ".org.hk",
            ".org.hn",
            ".org.ht",
            ".org.il",
            ".org.im",
            ".org.in",
            ".org.iq",
            ".org.ir",
            ".org.is",
            ".org.je",
            ".org.jo",
            ".org.kg",
            ".org.ki",
            ".org.km",
            ".org.kn",
            ".org.kp",
            ".org.ky",
            ".org.kz",
            ".org.la",
            ".org.lb",
            ".org.lc",
            ".org.lk",
            ".org.lr",
            ".org.ls",
            ".org.lv",
            ".org.ly",
            ".org.ma",
            ".org.me",
            ".org.mg",
            ".org.mk",
            ".org.ml",
            ".org.mn",
            ".org.mo",
            ".org.ms",
            ".org.mt",
            ".org.mu",
            ".org.mv",
            ".org.mw",
            ".org.mx",
            ".org.my",
            ".org.na",
            ".org.ng",
            ".org.nr",
            ".org.nz",
            ".org.om",
            ".org.pa",
            ".org.pe",
            ".org.pf",
            ".org.ph",
            ".org.pk",
            ".org.pl",
            ".org.pn",
            ".org.pr",
            ".org.ps",
            ".org.pt",
            ".org.py",
            ".org.qa",
            ".org.ro",
            ".org.rs",
            ".org.ru",
            ".org.sa",
            ".org.sb",
            ".org.sc",
            ".org.sd",
            ".org.se",
            ".org.sg",
            ".org.sh",
            ".org.sl",
            ".org.sn",
            ".org.so",
            ".org.st",
            ".org.sv",
            ".org.sy",
            ".org.sz",
            ".org.tj",
            ".org.tm",
            ".org.tn",
            ".org.to",
            ".org.tr",
            ".org.tt",
            ".org.tw",
            ".org.ua",
            ".org.ug",
            ".org.uy",
            ".org.uz",
            ".org.vc",
            ".org.ve",
            ".org.vi",
            ".org.vn",
            ".org.vu",
            ".org.ws",
            ".org.za",
            ".org.zm",
            ".pa",
            ".pe",
            ".pe.kr",
            ".pf",
            ".ph",
            ".pk",
            ".pm",
            ".pn",
            ".pp.ru",
            ".pr",
            ".ps",
            ".pw",
            ".py",
            ".qa",
            ".qc.ca",
            ".qh.cn",
            ".re",
            ".rw",
            ".sa",
            ".sb",
            ".sc",
            ".sc.cn",
            ".sch.lk",
            ".sd",
            ".sd.cn",
            ".sh",
            ".sh.cn",
            ".si",
            ".sj",
            ".sl",
            ".sm",
            ".sn",
            ".sn.cn",
            ".so",
            ".sr",
            ".ss",
            ".st",
            ".su",
            ".sv",
            ".sx",
            ".sx.cn",
            ".sy",
            ".sz",
            ".tc",
            ".td",
            ".tf",
            ".tg",
            ".th",
            ".tj",
            ".tj.cn",
            ".tl",
            ".tm",
            ".tm.fr",
            ".tm.mc",
            ".tm.se",
            ".tn",
            ".to",
            ".trd.br",
            ".tt",
            ".tw.cn",
            ".tz",
            ".ug",
            ".uy",
            ".uz",
            ".va",
            ".vc",
            ".ve",
            ".vg",
            ".vi",
            ".vn",
            ".vu",
            ".web.id",
            ".web.ve",
            ".wf",
            ".ws",
            ".xj.cn",
            ".xz.cn",
            ".ye",
            ".yn.cn",
            ".yt",
            ".zj.cn",
            ".zm",
            ".zw",
            ".xn--qxam",
            ".xn--90ais",
            ".xn--e1a4c",
            ".xn--80ao21a",
            ".xn--d1alf",
            ".xn--l1acc",
            ".xn--p1ai",
            ".xn--90a3ac",
            ".xn--j1amh",
            ".xn--y9a3aq",
            ".xn--node",
            ".xn--mgbayh7gpa",
            ".xn--lgbbat1ad8j",
            ".xn--mgberp4a5d4ar",
            ".xn--mgbc0a9azcg",
            ".xn--mgbaam7a8h",
            ".xn--mgba3a4f16a",
            ".xn--mgbbh1a71e",
            ".xn--mgbai9azgqp6j",
            ".xn--pgbs0dh",
            ".xn--mgbpl2fh",
            ".xn--ogbpf8fl",
            ".xn--mgbtx2b",
            ".xn--mgb9awbf",
            ".xn--ygbi2ammx",
            ".xn--wgbl6a",
            ".xn--wgbh1c",
            ".xn--mgbx4cd0ab",
            ".xn--h2brj9c",
            ".xn--54b7fta0cc",
            ".xn--45brj9c",
            ".xn--s9brj9c",
            ".xn--gecrj9c",
            ".xn--xkc2dl3a5ee0h",
            ".xn--xkc2al3hye2a",
            ".xn--clchc0ea0b2g2a9gcd",
            ".xn--fpcrj9c3d",
            ".xn--fzc2c9e2c",
            ".xn--o3cw4h",
            ".xn--3e0b707e",
            ".xn--fiqs8s",
            ".xn--fiqz9s",
           // ".xn--kprw13d",
            ".xn--kpry57d",
            ".xn--yfro4i67o",
            ".xn--mix891f",
            ".xn--j6w193g"
        };

}
