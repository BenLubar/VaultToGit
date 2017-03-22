using System;
using System.Collections.Generic;
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

            long start = 0;
            if (Directory.Exists("VaultToGitTemp"))
            {
                using (var count = Process.Start(new ProcessStartInfo("git", "rev-list --count master")
                {
                    WorkingDirectory = "VaultToGitTemp",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                }))
                {
                    var output = count.StandardOutput.ReadToEnd();
                    start = Convert.ToInt64(output.Trim(), 10);
                    count.WaitForExit();
                }
            }
            else
            {
                Directory.CreateDirectory("VaultToGitTemp");
                using (var init = Process.Start(new ProcessStartInfo("git", "init")
                {
                    WorkingDirectory = "VaultToGitTemp",
                    UseShellExecute = false,
                }))
                {
                    init.WaitForExit();
                }
                using (var init = Process.Start(new ProcessStartInfo("git", "config user.name \"VaultToGit Importer\"")
                {
                    WorkingDirectory = "VaultToGitTemp",
                    UseShellExecute = false,
                }))
                {
                    init.WaitForExit();
                }
                using (var init = Process.Start(new ProcessStartInfo("git", $"config user.email \"{Config.Instance.Username}@{Config.Instance.EmailDomain}\"")
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
                File.WriteAllText(@"VaultToGitTemp\.gitignore", @"_sg*
~sak*
*.tmp
*.bak

*.suo
*.clw
*.config
*.dca
*.dll
*.dsw
*.exe
*.hlp
*.incr
*.ncb
*.opt
*.pdb
*.plg
*.scc
*.suo
*.user
*.vbw
*.webuser

bin/
debug/
obj/
release/
.vs/
", System.Text.Encoding.UTF8);
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

                int rowsRetrieved = 0;
                string queryToken = null;
                for (; ; start += 1000)
                {
                    VaultTxHistoryItem[] history = null;
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
                    using (var gc = Process.Start(new ProcessStartInfo("git", "gc --auto")
                    {
                        WorkingDirectory = "VaultToGitTemp",
                        UseShellExecute = false,
                    }))
                    {
                        gc.WaitForExit();
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

                using (var count = Process.Start(new ProcessStartInfo("git", "rev-list --count master")
                {
                    WorkingDirectory = "VaultToGitTemp",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                }))
                {
                    var output = count.StandardOutput.ReadToEnd();
                    start = Convert.ToInt64(output.Trim(), 10);
                    count.WaitForExit();
                }

                Console.WriteLine($"Retrieving {start} transaction IDs...");
                VaultTxHistoryItem[] items = null;
                ci.Connection.VersionHistoryBegin((int)start, repositoryID, start, historyRequest, ref rowsRetrieved, ref queryToken);
                ci.Connection.VersionHistoryFetch(queryToken, 0, rowsRetrieved - 1, ref items);
                ci.Connection.VersionHistoryEnd(queryToken);

                var txIDs = new Dictionary<long, string>();
                foreach (var item in items)
                {
                    txIDs.Add(item.TxID, $"HEAD~{start - item.Version - 1}");
                }

                Console.WriteLine($"Processing labels...");
                ci.BeginLabelQuery(ci.Repository.Root.FullPath, ci.Repository.Root.ID, true, false, false, true, 999999999, out int rowsRetrievedInherited, out int rowsRetrievedRecursive, out queryToken);
                ci.GetLabelQueryItems_Recursive(queryToken, 0, rowsRetrievedRecursive - 1, out VaultLabelItemX[] labels);
                ci.EndLabelQuery(queryToken);
                foreach (var label in labels)
                {
                    var commit = txIDs[label.TxID];
                    if (commit == null)
                    {
                        Console.WriteLine($"Could not find commit for label {label.Label}");
                    }
                    using (var tag = Process.Start(new ProcessStartInfo("git", $"tag -f \"{label.Label}\" {commit}")
                    {
                        WorkingDirectory = "VaultToGitTemp",
                        UseShellExecute = false,
                    }))
                    {
                        tag.WaitForExit();
                    }
                }
            }
            finally
            {
                ServerOperations.Logout();
            }
        }
    }
}
