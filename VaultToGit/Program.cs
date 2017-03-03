using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using VaultClientIntegrationLib;
using VaultClientOperationsLib;
using VaultLib;

namespace VaultToGit
{
    class Program
    {
        static void Main(string[] args)
        {
            ServerOperations.SetLoginOptions(Config.Instance.URL, Config.Instance.Username, Config.Instance.Password, Config.Instance.Repository, false);
            try
            {
                Directory.Delete("VaultToGitTemp", true);
            }
            catch (DirectoryNotFoundException)
            {
                // ignore
            }

            Directory.CreateDirectory("VaultToGitTemp");
            using (var init = Process.Start(new ProcessStartInfo("git", "init")
            {
                WorkingDirectory = "VaultToGitTemp",
                UseShellExecute = false,
            }))
            {
                init.WaitForExit();
            }
            using (var init = Process.Start(new ProcessStartInfo("git", "lfs install")
            {
                WorkingDirectory = "VaultToGitTemp",
                UseShellExecute = false,
            }))
            {
                init.WaitForExit();
            }
            using (var init = Process.Start(new ProcessStartInfo("git", "lfs track *.png *.jpg *.gif *.ico *.zip *.snk *.so *.dylib *.dll *.exe")
            {
                WorkingDirectory = "VaultToGitTemp",
                UseShellExecute = false,
            }))
            {
                init.WaitForExit();
            }

            try
            {
                ServerOperations.Login();

                ServerOperations.GetInstance().UserMessage += (so, message) => Console.WriteLine(message);

                ServerOperations.SetWorkingFolder("$", Path.Combine(Directory.GetCurrentDirectory(), "VaultToGitTemp"), true, true);

                var ci = ServerOperations.client.ClientInstance;

                var repositoryID = ci.ActiveRepositoryID;

                var historyRequest = new VaultHistoryQueryRequest()
                {
                    TopID = ci.Repository.Root.ID,
                    BeginDate = VaultDate.EmptyDate(),
                    EndDate = VaultDate.EmptyDate(),
                    Sorts = new long[] { VaultQueryRequestSort.DateSort | VaultQueryRequestSort.AscSort },
                };

                var getOptions = new GetOptions()
                {
                    MakeWritable = MakeWritableType.MakeAllFilesWritable,
                    Merge = MergeType.OverwriteWorkingCopy,
                    PerformDeletions = PerformDeletionsType.RemoveWorkingCopy,
                    Recursive = true,
                    SetFileTime = SetFileTimeType.CheckIn,
                };

                for (long start = 0; ; start += 1000)
                {
                    VaultTxHistoryItem[] history = null;
                    int rowsRetrieved = 0;
                    string queryToken = null;
                    Console.WriteLine("Fetching history...");
                    ci.Connection.VersionHistoryBegin(1000, ci.ActiveRepositoryID, start, historyRequest, ref rowsRetrieved, ref queryToken);
                    try
                    {
                        if (rowsRetrieved > 0)
                        {
                            ci.Connection.VersionHistoryFetch(queryToken, 0, rowsRetrieved - 1, ref history);
                            Console.WriteLine($"Retrieved history records {history.First().Version} to {history.Last().Version} of {ci.Repository.Root.Version}.");
                        }
                        else
                        {
                            Console.WriteLine("Reached end of history.");
                            break;
                        }
                    }
                    finally
                    {
                        ci.Connection.VersionHistoryEnd(queryToken);
                    }

                    foreach (var version in history)
                    {
                        GetOperations.ProcessCommandGetVersion("$", (int)version.Version, getOptions);
                        using (var add = Process.Start(new ProcessStartInfo("git", "add --all .")
                        {
                            WorkingDirectory = "VaultToGitTemp",
                            UseShellExecute = false,
                        }))
                        {
                            add.WaitForExit();
                        }
                        using (var commit = Process.Start(new ProcessStartInfo("git", $"commit --allow-empty --allow-empty-message --file=- --author=\"{version.UserName} <{version.UserLogin}@{Config.Instance.EmailDomain}>\" --date=\"{version.TxDate.GetDateTime().ToString(System.Globalization.DateTimeFormatInfo.InvariantInfo.UniversalSortableDateTimePattern)}\"")
                        {
                            WorkingDirectory = "VaultToGitTemp",
                            UseShellExecute = false,
                            RedirectStandardInput = true,
                        }))
                        {
                            commit.StandardInput.WriteLine(version.Comment ?? "");
                            commit.StandardInput.Close();
                            commit.WaitForExit();
                        }
                    }
                }

                using (var gc = Process.Start(new ProcessStartInfo("git", "gc --aggressive")
                {
                    WorkingDirectory = "VaultToGitTemp",
                    UseShellExecute = false,
                }))
                {
                    gc.WaitForExit();
                }
            }
            finally
            {
                ServerOperations.Logout();
            }
        }
    }
}
