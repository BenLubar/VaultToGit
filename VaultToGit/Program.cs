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
            foreach (var repository in Config.Instance.Repositories)
            {
                ServerOperations.SetLoginOptions(Config.Instance.URL, Config.Instance.Username, Config.Instance.Password, repository.Key, false);

                long start = 0;
                if (Directory.Exists(repository.Value))
                {
                    using (var count = Process.Start(new ProcessStartInfo("git", "rev-list --count master")
                    {
                        WorkingDirectory = repository.Value,
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
                    Directory.CreateDirectory(repository.Value);
                    using (var init = Process.Start(new ProcessStartInfo("git", "init")
                    {
                        WorkingDirectory = repository.Value,
                        UseShellExecute = false,
                    }))
                    {
                        init.WaitForExit();
                    }
                    using (var init = Process.Start(new ProcessStartInfo("git", "config user.name \"VaultToGit Importer\"")
                    {
                        WorkingDirectory = repository.Value,
                        UseShellExecute = false,
                    }))
                    {
                        init.WaitForExit();
                    }
                    using (var init = Process.Start(new ProcessStartInfo("git", $"config user.email \"{Config.Instance.Username}@{Config.Instance.EmailDomain}\"")
                    {
                        WorkingDirectory = repository.Value,
                        UseShellExecute = false,
                    }))
                    {
                        init.WaitForExit();
                    }
                    using (var init = Process.Start(new ProcessStartInfo("git", "lfs install")
                    {
                        WorkingDirectory = repository.Value,
                        UseShellExecute = false,
                    }))
                    {
                        init.WaitForExit();
                    }
                    using (var init = Process.Start(new ProcessStartInfo("git", "lfs track *.png *.jpg *.gif *.ico *.zip *.snk *.so *.dylib *.dll *.exe")
                    {
                        WorkingDirectory = repository.Value,
                        UseShellExecute = false,
                    }))
                    {
                        init.WaitForExit();
                    }
                    File.WriteAllText(Path.Combine(repository.Value, ".gitignore"), @"_sg*
~sak*
*.tmp
*.bak

*.suo
*.clw
*.dca
*.dsw
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

                    ServerOperations.SetWorkingFolder("$", Path.Combine(Directory.GetCurrentDirectory(), repository.Value), true, true);

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
                            try
                            {
                                GetOperations.ProcessCommandGetVersion("$", (int)version.Version, getOptions);

                                foreach (var item in ServerOperations.ProcessCommandTxDetail(version.TxID).items)
                                {
                                    string requestType;
                                    switch (item.RequestType)
                                    {
                                        case VaultRequestType.Delete:
                                            requestType = "delete";
                                            break;
                                        case VaultRequestType.Move:
                                            requestType = "move";
                                            break;
                                        case VaultRequestType.Rename:
                                            requestType = "rename";
                                            break;
                                        default:
                                            continue;
                                    }
                                    var path = Path.Combine(Directory.GetCurrentDirectory(), repository.Value, item.ItemPath1.Substring("$/".Length));
                                    if (File.Exists(path))
                                    {
                                        Console.WriteLine($"Vault forgot to {requestType} file {item.ItemPath1}.");
                                        File.Delete(path);
                                    }
                                    if (Directory.Exists(path))
                                    {
                                        Console.WriteLine($"Vault forgot to {requestType} directory {item.ItemPath1}.");
                                        Directory.Delete(path, true);
                                    }
                                }
                            }
                            catch (Exception ex) when (ex.Message.StartsWith("There is no version "))
                            {
                                try
                                {
                                    ci.Connection.Ping();
                                }
                                catch (Exception ex2)
                                {
                                    throw new AggregateException("Vault seems to be down.", ex, ex2);
                                }
                                Console.WriteLine($"Assuming version {version.Version} was deleted.");
                            }
                            using (var add = Process.Start(new ProcessStartInfo("git", "add --all .")
                            {
                                WorkingDirectory = repository.Value,
                                UseShellExecute = false,
                            }))
                            {
                                add.WaitForExit();
                            }
                            using (var commit = Process.Start(new ProcessStartInfo("git", $"commit --allow-empty --allow-empty-message --file=- --author=\"{version.UserName} <{version.UserLogin}@{Config.Instance.EmailDomain}>\" --date=\"{version.TxDate.GetDateTime().ToString(System.Globalization.DateTimeFormatInfo.InvariantInfo.UniversalSortableDateTimePattern)}\"")
                            {
                                WorkingDirectory = repository.Value,
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
                            WorkingDirectory = repository.Value,
                            UseShellExecute = false,
                        }))
                        {
                            gc.WaitForExit();
                        }
                    }

                    using (var gc = Process.Start(new ProcessStartInfo("git", "gc --aggressive")
                    {
                        WorkingDirectory = repository.Value,
                        UseShellExecute = false,
                    }))
                    {
                        gc.WaitForExit();
                    }

                    using (var count = Process.Start(new ProcessStartInfo("git", "rev-list --count master")
                    {
                        WorkingDirectory = repository.Value,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                    }))
                    {
                        var output = count.StandardOutput.ReadToEnd();
                        start = Convert.ToInt64(output.Trim(), 10);
                        count.WaitForExit();
                    }

                    Console.WriteLine($"Retrieving {start} transaction IDs...");
                    var txIDs = new Dictionary<long, string>();
                    for (long txStart = 0; txStart < start; txStart += 1000)
                    {
                        VaultTxHistoryItem[] items = null;
                        ci.Connection.VersionHistoryBegin(1000, repositoryID, txStart, historyRequest, ref rowsRetrieved, ref queryToken);
                        ci.Connection.VersionHistoryFetch(queryToken, 0, rowsRetrieved - 1, ref items);
                        ci.Connection.VersionHistoryEnd(queryToken);

                        foreach (var item in items)
                        {
                            txIDs[item.TxID] = $"HEAD~{start - item.Version - 1}";
                        }
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
                            continue;
                        }
                        using (var tag = Process.Start(new ProcessStartInfo("git", $"tag -f \"{label.Label.Replace(' ', '-')}\" {commit}")
                        {
                            WorkingDirectory = repository.Value,
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
}
