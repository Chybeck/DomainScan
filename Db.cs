using MySql.Data.MySqlClient;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace DomainDb
{
    public class Domain
    {
        public string DomainName;
        public int Id;
        public DateTime? LastDate;
        public DateTime? FirstDate;
        public bool NsRecorded;
        public string Processing;
        public string Extension;
        public string Libelle;
    }
    public class Db : IDisposable
    {
        private readonly bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        public static string ConnectionString { get; set; }
        public object sql { get; set; }

        private readonly MySqlConnection _con;

        public Db()
        {
            for (int attempts = 0; attempts < 5; attempts++)
            {
                var keep_attempt = false;
                try
                {
                    _con = new MySqlConnection(ConnectionString);
                    _con.Open();
                }
                catch (Exception e)
                {
                    System.Console.WriteLine("Crash connexion SQL");
                    System.Console.WriteLine(e);
                    System.Console.WriteLine("RETRY dans 10sec...");
                    Thread.Sleep(10000);
                    keep_attempt = true;
                    //System.Environment.Exit(1);
                }
                finally
                {
                    if (keep_attempt == false) attempts = 5;
                }

            }
        }

        public void Dispose()
        {
            _con.Close();
        }

        public void Init()
        {
            using (var cmd = _con.CreateCommand())
            {
                cmd.CommandText = "show tables";
                //cmd.CommandText = "truncate table libelles";
                try
                {
                    cmd.ExecuteNonQuery();
                }

                catch (Exception e)
                {
                    System.Console.WriteLine("Crash connexion SQL");
                    System.Console.WriteLine(e);
                    System.Environment.Exit(1);
                }

            }
        }




        public List<Domain> GetForProcessing(string workstationName, int limit)
        {
            var results = new List<Domain>();

            using (var cmd = _con.CreateCommand())
            {

                cmd.CommandText = "update libelles IGNORE INDEX (Processing) set Processing = @wksname where Processing is null order by LastDate limit @limit";
                cmd.Parameters.AddWithValue("@wksname", workstationName);
                cmd.Parameters.AddWithValue("@limit", limit);
                cmd.CommandTimeout = 300;

                try
                {
                    cmd.ExecuteNonQuery();
                }

                catch (Exception e)
                {
                    System.Console.WriteLine("Crash UPDATE");
                    System.Console.WriteLine(e);
                    System.Environment.Exit(1);
                }


            }
            System.Console.WriteLine("Selection des Updates...");

            using (var cmd = _con.CreateCommand())
            {
                //command.CommandText = "select Libelle,lastDate,Processing from libelles order by LastDate ASC limit @limit";
                cmd.CommandText = "select Libelle,LastDate,Processing from libelles WHERE Processing = @wksname";
                cmd.Parameters.AddWithValue("@wksname", workstationName);
                //command.Parameters.AddWithValue("@limit", limit);
                cmd.CommandTimeout = 30;

                try
                {

                    using (var reader = cmd.ExecuteReader())
                    {
                        System.Console.WriteLine("Execution...");
                        if (reader.HasRows)
                        {
                            while (reader.Read())
                            {
                                var domain = new Domain
                                {
                                    Libelle = reader.GetString("Libelle"),
                                };

                                if (!reader.IsDBNull(2))
                                    domain.Processing = reader.GetString("Processing");

                                if (!reader.IsDBNull(1))
                                    domain.LastDate = reader.GetDateTime("LastDate");
                                else
                                    domain.LastDate = null;

                                results.Add(domain);
                            }
                        }
                        else
                        {
                            Console.WriteLine("Erreur: 0 rows selectionnes");
                            System.Threading.Thread.Sleep(2000);
                        }
                    } // using
                } // try
                catch (Exception)
                {
                    //System.Console.WriteLine("Crash SELECT des UPDATE");
                    //System.Console.WriteLine(e);
                }
            }
            //System.Console.WriteLine("Retour...");
            return results;
        }


        public void SaveLibelle(string libelle)
        {
            using (var command = _con.CreateCommand())
            {
                command.CommandText = "update libelles set Processing = NULL, LastDate = @lastDate where Libelle = @libelle";
                command.Parameters.AddWithValue("@libelle", libelle);
                //command.Parameters.AddWithValue("@lastDate", DateTime.Now);
                if (isWindows)
                    command.Parameters.AddWithValue("@lastDate", TimeZoneInfo.ConvertTime(DateTime.Now, TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time")));
                else
                    command.Parameters.AddWithValue("@lastDate", TimeZoneInfo.ConvertTime(DateTime.Now, TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris")));

                command.ExecuteNonQueryAsync();
            }
        }

        public void ResetLibelle(string Processing)
        {
            using (var command = _con.CreateCommand())
            {
                command.CommandText = "update libelles set Processing = NULL, LastDate = @lastDate where Processing = @Processing";
                command.Parameters.AddWithValue("@Processing", Processing);
                //command.Parameters.AddWithValue("@lastDate", DateTime.Now);
                if (isWindows)
                    command.Parameters.AddWithValue("@lastDate", TimeZoneInfo.ConvertTime(DateTime.Now, TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time")));
                else
                    command.Parameters.AddWithValue("@lastDate", TimeZoneInfo.ConvertTime(DateTime.Now, TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris")));

                command.CommandTimeout = 300;
                //command.ExecuteNonQueryAsync();
                try
                {
                    //command.ExecuteNonQueryAsync();
                    command.ExecuteNonQuery();
                }
                catch (Exception e)
                {
                    Console.WriteLine("SQL RESET LIBELLE " + e);
                }


            }
        }



        public bool Save(Domain domain)
        {
            using (var command = _con.CreateCommand())
            {
                command.CommandText = "select DomainName from domains where DomainName = @domainName";
                command.Parameters.AddWithValue("@domainName", domain.DomainName);
                if (command.ExecuteScalar() != null)
                {
                    using (var command2 = _con.CreateCommand())
                    {
                        command2.CommandText = "update domains set Extension = @Extension, LastDate = @lastDate, NsRecorded = @nsRecorded where DomainName = @DomainName";
                        command2.Parameters.AddWithValue("@lastDate", domain.LastDate);
                        command2.Parameters.AddWithValue("@nsRecorded", domain.NsRecorded);
                        command2.Parameters.AddWithValue("@DomainName", domain.DomainName);
                        command2.Parameters.AddWithValue("@Extension", domain.Extension);

                        try
                        {
                            command2.ExecuteNonQueryAsync();
                        }
                        catch (Exception e)
                        {
                            // ignored
                            Console.WriteLine("SQL 1 " + e);
                            //Console.WriteLine();
                        }
                        return false;
                    }
                }
                else
                {
                    using (var command3 = _con.CreateCommand())
                    {
                        command3.CommandText = "insert into domains (DomainName,Extension,NsRecorded,Origin) values (@DomainName,@Extension,@nsRecorded,'scan')";
                        command3.Parameters.AddWithValue("@nsRecorded", domain.NsRecorded);
                        command3.Parameters.AddWithValue("@DomainName", domain.DomainName);
                        command3.Parameters.AddWithValue("@Extension", domain.Extension);

                        try
                        {
                            return command3.ExecuteNonQuery() == 1;
                        }

                        catch (Exception e)
                        {
                            Console.WriteLine("SQL 2 " + e);
                            return false;
                        }
                    }
                }
            }
        }



    }
}