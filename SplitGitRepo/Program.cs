﻿using LibGit2Sharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SplitGitRepo
{
    class Program
    {
        private static readonly string RepoPrefix = "git@github.com:Inedo/";

        private struct SharedRepo
        {
            public SharedRepo(string name, params string[] paths)
            {
                this.Name = name;
                this.Paths = paths;
                this.Commits = new List<Tuple<ObjectId, ObjectId>>();
            }

            public string Name { get; }
            public string[] Paths { get; }
            public List<Tuple<ObjectId, ObjectId>> Commits { get; }
        }

        static void Main(string[] args)
        {
            using (var baseRepo = new Repository("VaultToGitTemp"))
            {
                var commits = baseRepo.Commits.QueryBy(new CommitFilter { SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Reverse });
                Console.WriteLine("Enumerating root directories...");
                var rootDirectories = commits.SelectMany(c => c.Tree.Where(t => t.TargetType == TreeEntryTargetType.Tree && !t.Name.Contains('-')).Select(t => t.Path)).Distinct().OrderBy(s => s).ToList();
                Console.WriteLine($"Found {rootDirectories.Count} root directories.");
                var shared = new[]
                {
                    new SharedRepo("BuildMasterOtter.Web", "BuildMaster/BuildMasterSolution/Web/BuildMasterOtter.Web", "Otter/src/BuildMasterOtter.Web"),
                    new SharedRepo("BuildMasterOtterProget.Web", "BuildMaster/BuildMasterSolution/Web/BuildMasterOtterProGet.Web", "ProGet/BuildMasterOtterProGet.Web", "Otter/src/BuildMasterOtterProGet.Web"),
                    new SharedRepo("InedoLibWeb", "Otter/src/InedoLibWeb", "ProGet/InedoLibWeb"),
                    new SharedRepo("LESSCompiler", "Otter/LESSCompiler", "Crm1000/LESSCompiler", "InedoLib/LESSCompiler", "ProGet/LESSCompiler", "BuildMaster/LESSCompiler", "ChangeVision/AstahWebsite/Resources/LESSCompiler", "Inedo/inedo.com/Inedo.Com.Web.WebApplication/Resources/LESSCompiler"),
                    new SharedRepo("Consolation", "Otter/src/romp/Consolation", "ProGet/ProGet.Client/Consolation", "Inedo/incluser/incluser/Consolation", "Inedo.Agents/Inedo.Agents.Service/Consolation"),
                    new SharedRepo("TypeDefinitions", "ProGet/ProGet.WebApplication/Resources/TypeDefinitions", "Crm1000/Web/Resources/TypeDefinitions"),
                    new SharedRepo("WindowsServices", "InedoLib/InedoLib.NET45/WindowsServices", "Otter/src/Inedo.Agents.Service/SharedWithInedoLib/WindowsServices"),
                };
                Console.WriteLine("Clearing output directory...");
                if (Directory.Exists("output"))
                {
                    Directory.Delete("output", true);
                }
                Directory.CreateDirectory("output");
                foreach (var s in shared)
                {
                    Console.WriteLine($"Splitting {s.Name} (shared by {s.Paths.Length} projects)...");
                    s.Commits.AddRange(SplitRepository(baseRepo, commits, s.Name, s.Paths[0], Enumerable.Empty<SharedRepo>()));
                    Console.WriteLine($"Split {s.Commits.Count} commits.");
                }
                foreach (var root in rootDirectories)
                {
                    Console.WriteLine($"Splitting project repository {root}...");
                    var commitMapping = SplitRepository(baseRepo, commits, root, root, shared).ToDictionary(t => t.Item1, t => t.Item2);
                    Console.WriteLine($"Split {commitMapping.Count} commits.");

                    using (var repo = new Repository(Path.Combine("output", root)))
                    {
                        Console.WriteLine($"Converting tags with prefix {root}-...");
                        foreach (var tag in baseRepo.Tags.Where(t => t.FriendlyName.StartsWith(root + "-")))
                        {
                            if (commitMapping.ContainsKey(new ObjectId(tag.Target.Sha)))
                            {
                                repo.Tags.Add(tag.FriendlyName, commitMapping[new ObjectId(tag.Target.Sha)].Sha);
                                Console.WriteLine($"Converted tag {tag.FriendlyName}");
                            }
                            else
                            {
                                Console.WriteLine($"Warning: No commit for tag {tag.FriendlyName}");
                            }
                        }
                    }
                }
            }
        }

        private static IEnumerable<Tuple<string, Blob>> RecursiveTree(Tree tree, string prefix)
        {
            foreach (var entry in tree)
            {
                if (entry.TargetType == TreeEntryTargetType.Tree)
                {
                    foreach (var t in RecursiveTree((Tree)entry.Target, prefix))
                    {
                        yield return t;
                    }
                }
                else if (entry.TargetType == TreeEntryTargetType.Blob)
                {
                    yield return new Tuple<string, Blob>(entry.Path.Substring(prefix.Length + 1), (Blob)entry.Target);
                }
            }
        }

        private static IEnumerable<Tuple<ObjectId, ObjectId>> SplitRepository(Repository baseRepo, ICommitLog commits, string name, string path, IEnumerable<SharedRepo> sharedRepos)
        {
            var shared = sharedRepos.Where(r => r.Paths.Any(p => p.StartsWith(path + "/"))).Select(r => new
            {
                Repo = RepoPrefix + r.Name,
                Path = r.Paths.First(p => p.StartsWith(path + "/")).Substring(path.Length + 1),
                Commits = r.Commits
            });

            using (var repo = new Repository(Repository.Init(Path.Combine("output", name))))
            {
                File.Copy(Path.Combine("VaultToGitTemp", ".gitignore"), Path.Combine("output", name, ".gitignore"));
                repo.Index.Add(".gitignore");
                File.Copy(Path.Combine("VaultToGitTemp", ".gitattributes"), Path.Combine("output", name, ".gitattributes"));
                repo.Index.Add(".gitattributes");

                if (shared.Any())
                {
                    File.WriteAllText(Path.Combine("output", name, ".gitmodules"), string.Join("", shared.Select(s => $"[submodule \"{s.Path}\"]\n    path = {s.Path}\n    url = {s.Repo}\n")));
                    repo.Index.Add(".gitmodules");
                    foreach (var s in shared)
                    {
                        repo.Submodules.Init(s.Path, false);
                    }
                }

                foreach (var c in commits)
                {
                    if (c.Tree[path] == null)
                    {
                        continue;
                    }

                    TreeDefinition tree = null;
                    foreach (var s in shared)
                    {
                        var mapping = s.Commits.FirstOrDefault(commit => c.Id == commit.Item1);
                        if (mapping != null)
                        {
                            if (tree == null)
                            {
                                var tempCommit = repo.Commit("temp", new Signature("temp", "temp@example.com", DateTimeOffset.UtcNow), new Signature("temp", "temp@example.com", DateTimeOffset.UtcNow), new CommitOptions { AllowEmptyCommit = true });
                                tree = TreeDefinition.From(tempCommit);
                                repo.Reset(ResetMode.Soft, tempCommit.Parents.First());
                            }
                            tree.AddGitLink(s.Path, mapping.Item2);
                        }
                    }
                    if (tree != null)
                    {
                        repo.Index.Replace(repo.ObjectDatabase.CreateTree(tree));
                    }

                    var oldTree = c.Parents.FirstOrDefault()?.Tree?[path]?.Target as Tree;
                    if (oldTree != null)
                    {
                        var diff = baseRepo.Diff.Compare<TreeChanges>(oldTree, (Tree)c.Tree[path].Target);
                        if (!diff.Any())
                        {
                            continue;
                        }
                        foreach (var file in diff)
                        {
                            if (file.Mode == Mode.Directory)
                            {
                                continue;
                            }

                            if (!file.Exists || (file.OldPath != file.Path && file.Status != ChangeKind.Copied))
                            {
                                if (!shared.Any(s => file.OldPath.Replace('\\', '/').StartsWith(s.Path + '/')))
                                {
                                    File.Delete(Path.Combine("output", name, file.OldPath));
                                    repo.Index.Remove(file.OldPath);
                                }
                            }

                            if (file.Exists)
                            {
                                if (!shared.Any(s => file.Path.Replace('\\', '/').StartsWith(s.Path + '/')))
                                {
                                    Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine("output", name, file.Path)));
                                    using (var input = baseRepo.Lookup<Blob>(file.Oid).GetContentStream())
                                    using (var output = new FileStream(Path.Combine("output", name, file.Path), FileMode.OpenOrCreate, FileAccess.Write))
                                    {
                                        input.CopyTo(output);
                                    }
                                    repo.Index.Add(file.Path);
                                }
                            }
                        }
                    }
                    else
                    {
                        foreach (var entry in RecursiveTree((Tree)c.Tree[path].Target, path))
                        {
                            if (shared.Any(s => entry.Item1.Replace('\\', '/').StartsWith(s.Path + '/')))
                            {
                                continue;
                            }
                            var fullPath = Path.Combine("output", name, entry.Item1);
                            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                            using (var content = entry.Item2.GetContentStream())
                            using (var output = File.Create(fullPath))
                            {
                                content.CopyTo(output);
                            }
                            repo.Index.Add(entry.Item1);
                        }
                    }

                    var rewrittenCommit = repo.Commit(c.Message, c.Author, c.Committer, new CommitOptions { AllowEmptyCommit = true });
                    yield return new Tuple<ObjectId, ObjectId>(c.Id, rewrittenCommit.Id);
                }
            }
        }
    }
}
