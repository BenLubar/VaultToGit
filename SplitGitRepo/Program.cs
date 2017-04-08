using LibGit2Sharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SplitGitRepo
{
    struct SharedRepo
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

    struct MergedRepo
    {
        public MergedRepo(string name, string repo, IDictionary<string, string> mapping)
        {
            this.Name = name;
            this.Repo = repo;
            this.Mapping = mapping;
        }

        public string Name { get; }
        public string Repo { get; }
        public IDictionary<string, string> Mapping { get; }
    }

    class Program
    {
        static void Main(string[] args)
        {
            using (var baseRepo = new Repository(Config.Instance.MainRepo))
            {
                var commits = baseRepo.Commits.QueryBy(new CommitFilter { SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Reverse });
                Console.WriteLine("Enumerating root directories...");
                var rootDirectories = commits.SelectMany(c => c.Tree.Where(t => t.TargetType == TreeEntryTargetType.Tree && !t.Name.Contains('-')).Select(t => t.Path)).Distinct().OrderBy(s => s).ToList();
                Console.WriteLine($"Found {rootDirectories.Count} root directories.");
                Console.WriteLine("Clearing output directory...");
                if (Directory.Exists("output"))
                {
                    Directory.Delete("output", true);
                }
                Directory.CreateDirectory("output");
                foreach (var s in Config.Instance.Shared)
                {
                    Console.WriteLine($"Splitting {s.Name} (shared by {s.Paths.Length} projects)...");
                    s.Commits.AddRange(SplitRepository(baseRepo, commits, s.Name, s.Paths[0], Enumerable.Empty<SharedRepo>(), Config.Instance.Merge));
                    Console.WriteLine($"Split {s.Commits.Count} commits.");
                }
                foreach (var root in rootDirectories)
                {
                    Console.WriteLine($"Splitting project repository {root}...");
                    var commitMapping = SplitRepository(baseRepo, commits, root, root, Config.Instance.Shared, Config.Instance.Merge).ToDictionary(t => t.Item1, t => t.Item2);
                    Console.WriteLine($"Split {commitMapping.Count} commits.");

                    using (var repo = new Repository(Path.Combine("output", root)))
                    {
                        Console.WriteLine($"Converting tags with prefix {root}-...");
                        foreach (var toMerge in Config.Instance.Merge)
                        {
                            if (toMerge.Name == root)
                            {
                                using (var mergeRepo = new Repository(toMerge.Repo))
                                {
                                    foreach (var tag in mergeRepo.Tags.Where(t => t.FriendlyName.StartsWith(root + "-")))
                                    {
                                        if (commitMapping.ContainsKey(new ObjectId(tag.Target.Sha)))
                                        {
                                            repo.Tags.Add(tag.FriendlyName, commitMapping[new ObjectId(tag.Target.Sha)].Sha);
                                            Console.WriteLine($"Converted tag {tag.FriendlyName} from {toMerge.Repo}");
                                        }
                                        else
                                        {
                                            int count = 0;
                                            var target = tag.Target as Commit;
                                            while (target != null)
                                            {
                                                if (commitMapping.ContainsKey(new ObjectId(target.Sha)))
                                                {
                                                    break;
                                                }
                                                count++;
                                                target = target.Parents.FirstOrDefault();
                                            }
                                            if (target != null)
                                            {
                                                repo.Tags.Add(tag.FriendlyName, commitMapping[new ObjectId(target.Sha)].Sha);
                                                Console.WriteLine($"Converted tag {tag.FriendlyName} from {toMerge.Repo} (went back {count} commits)");
                                            }
                                            else
                                            {
                                                Console.WriteLine($"Warning: No commit for tag {tag.FriendlyName} from {toMerge.Repo}");
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        foreach (var tag in baseRepo.Tags.Where(t => t.FriendlyName.StartsWith(root + "-")))
                        {
                            if (commitMapping.ContainsKey(new ObjectId(tag.Target.Sha)))
                            {
                                repo.Tags.Add(tag.FriendlyName, commitMapping[new ObjectId(tag.Target.Sha)].Sha);
                                Console.WriteLine($"Converted tag {tag.FriendlyName}");
                            }
                            else
                            {
                                int count = 0;
                                var target = tag.Target as Commit;
                                while (target != null)
                                {
                                    if (commitMapping.ContainsKey(new ObjectId(target.Sha)))
                                    {
                                        break;
                                    }
                                    count++;
                                    target = target.Parents.FirstOrDefault();
                                }
                                if (target != null)
                                {
                                    repo.Tags.Add(tag.FriendlyName, commitMapping[new ObjectId(target.Sha)].Sha);
                                    Console.WriteLine($"Converted tag {tag.FriendlyName} (went back {count} commits)");
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

        private static IEnumerable<Tuple<ObjectId, ObjectId>> SplitRepository(Repository baseRepo, ICommitLog commits, string name, string path, IEnumerable<SharedRepo> sharedRepos, IEnumerable<MergedRepo> mergedRepos)
        {
            var shared = sharedRepos.Where(r => r.Paths.Any(p => p.StartsWith(path + "/"))).Select(r => new
            {
                Repo = "../" + r.Name + ".git",
                Path = r.Paths.First(p => p.StartsWith(path + "/")).Substring(path.Length + 1),
                Commits = r.Commits
            });
            var merged = mergedRepos.Where(r => r.Name == name);

            using (var repo = new Repository(Repository.Init(Path.Combine("output", name))))
            {
                File.Copy(Path.Combine(Config.Instance.MainRepo, ".gitignore"), Path.Combine("output", name, ".gitignore"));
                repo.Index.Add(".gitignore");
                File.Copy(Path.Combine(Config.Instance.MainRepo, ".gitattributes"), Path.Combine("output", name, ".gitattributes"));
                repo.Index.Add(".gitattributes");

                foreach (var toMerge in merged)
                {
                    using (var mergeRepo = new Repository(toMerge.Repo))
                    {
                        foreach (var c in mergeRepo.Commits.QueryBy(new CommitFilter { SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Reverse }))
                        {
                            bool any = false;

                            foreach (var m in toMerge.Mapping)
                            {
                                if (Merge(repo, c, m.Key, m.Value))
                                {
                                    any = true;
                                }
                            }

                            if (any)
                            {
                                var rewrittenCommit = repo.Commit(c.Message, new Signature(c.Author.Name, Config.Instance.MapEmail(c.Author.Email), c.Author.When), new Signature(c.Committer.Name, Config.Instance.MapEmail(c.Author.Email), c.Author.When), new CommitOptions { AllowEmptyCommit = true });
                                yield return new Tuple<ObjectId, ObjectId>(c.Id, rewrittenCommit.Id);
                            }
                        }
                    }
                    repo.Index.Clear();
                    repo.Index.Add(".gitignore");
                    repo.Index.Add(".gitattributes");
                }

                if (shared.Any())
                {
                    File.WriteAllText(Path.Combine("output", name, ".gitmodules"), string.Join("", shared.Select(s => $"[submodule \"{s.Path}\"]\n    path = {s.Path}\n    url = {s.Repo}\n")));
                    repo.Index.Add(".gitmodules");
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

                    var email = Config.Instance.MapEmail(c.Author.Email);
                    var rewrittenCommit = repo.Commit(c.Message, new Signature(c.Author.Name, email, c.Author.When), new Signature(c.Committer.Name, email, c.Author.When), new CommitOptions { AllowEmptyCommit = true });
                    yield return new Tuple<ObjectId, ObjectId>(c.Id, rewrittenCommit.Id);
                }

                repo.Network.Remotes.Add("origin", Config.Instance.Origin(name));

                Console.WriteLine("Copying LFS files...");
                var lfsCount = CopyLfsFiles(repo, Path.Combine("output", name, ".git", "lfs", "objects"), new[] { Path.Combine(baseRepo.Info.WorkingDirectory, ".git", "lfs", "objects") }.Concat(merged.Where(r => r.Name == name).Select(r => Path.Combine(r.Repo, ".git", "lfs", "objects"))));
                Console.WriteLine($"Copied {lfsCount} files.");

                // LibGit2Sharp doesn't support git gc, so we use the command line:
                using (var gc = Process.Start(new ProcessStartInfo("git", "gc --aggressive")
                {
                    WorkingDirectory = repo.Info.WorkingDirectory,
                    UseShellExecute = false
                }))
                {
                    gc.WaitForExit();
                }
            }
        }

        private static bool Merge(Repository repo, Commit c, string src, string dst)
        {
            var newObject = c.Tree[src];
            var oldObject = c.Parents.FirstOrDefault()?.Tree?[src];
            if (newObject == null)
            {
                return false;
            }
            if (newObject.TargetType != oldObject?.TargetType)
            {
                if (oldObject != null)
                {
                    repo.Index.Remove(dst);
                }

                if (newObject.TargetType == TreeEntryTargetType.Tree)
                {
                    Merge(repo, null, (Tree)newObject.Target, null, newObject.Mode, dst);
                }
                else if (newObject.TargetType == TreeEntryTargetType.Blob)
                {
                    Merge(repo, null, (Blob)newObject.Target, null, newObject.Mode, dst);
                }
                return true;
            }
            if (newObject.TargetType == TreeEntryTargetType.Tree)
            {
                return Merge(repo, (Tree)oldObject.Target, (Tree)newObject.Target, oldObject.Mode, newObject.Mode, dst);
            }
            if (newObject.TargetType == TreeEntryTargetType.Blob)
            {
                return Merge(repo, (Blob)oldObject.Target, (Blob)newObject.Target, oldObject.Mode, newObject.Mode, dst);
            }
            return false;
        }

        private static bool Merge(Repository repo, Tree left, Tree right, Mode? oldMode, Mode newMode, string dst)
        {
            if (left == null)
            {
                foreach (var entry in right)
                {
                    if (entry.TargetType == TreeEntryTargetType.Tree)
                    {
                        Merge(repo, null, (Tree)entry.Target, null, entry.Mode, Path.Combine(dst, entry.Name));
                    }
                    else if (entry.TargetType == TreeEntryTargetType.Blob)
                    {
                        Merge(repo, null, (Blob)entry.Target, null, entry.Mode, Path.Combine(dst, entry.Name));
                    }
                }
                return true;
            }

            bool any = false;
            foreach (var leftEntry in left)
            {
                var rightEntry = right[leftEntry.Name];
                if (leftEntry.TargetType != rightEntry?.TargetType)
                {
                    repo.Index.Remove(Path.Combine(dst, leftEntry.Name));
                    any = true;
                }
            }

            foreach (var rightEntry in right)
            {
                var leftEntry = left[rightEntry.Name];
                if (rightEntry.TargetType == TreeEntryTargetType.Tree)
                {
                    if (Merge(repo, leftEntry?.Target as Tree, (Tree)rightEntry.Target, leftEntry?.Mode, rightEntry.Mode, Path.Combine(dst, rightEntry.Name)))
                    {
                        any = true;
                    }
                }
                else if (rightEntry.TargetType == TreeEntryTargetType.Blob)
                {
                    if (Merge(repo, leftEntry?.Target as Blob, (Blob)rightEntry.Target, leftEntry?.Mode, rightEntry.Mode, Path.Combine(dst, rightEntry.Name)))
                    {
                        any = true;
                    }
                }
            }

            return any;
        }

        private static bool Merge(Repository repo, Blob left, Blob right, Mode? oldMode, Mode newMode, string dst)
        {
            if (left?.Id == right.Id && oldMode == newMode)
            {
                return false;
            }

            Blob blob;
            using (var input = right.GetContentStream())
            {
                blob = repo.ObjectDatabase.CreateBlob(input);
            }
            repo.Index.Add(blob, dst, newMode);
            return true;
        }

        private static readonly Regex LfsFilePattern = new Regex(@"\.(exe|ico|zip|snk|dylib|dll|png|jpg|gif|so)$", RegexOptions.Compiled);
        private static int CopyLfsFiles(Repository repo, string dest, IEnumerable<string> sources)
        {
            int count = 0;
            foreach (var commit in repo.Commits)
            {
                count += CopyLfsFiles(commit.Tree, dest, sources);
            }
            return count;
        }

        private static int CopyLfsFiles(Tree tree, string dest, IEnumerable<string> sources)
        {
            int count = 0;

            foreach (var entry in tree)
            {
                if (entry.TargetType == TreeEntryTargetType.Blob && LfsFilePattern.IsMatch(entry.Name))
                {
                    var lines = ((Blob)entry.Target).GetContentText().Split('\n');
                    if (lines.All(string.IsNullOrWhiteSpace))
                    {
                        continue;
                    }
                    var oid = lines.ElementAt(1).Trim();
                    if (!oid.StartsWith("oid sha256:"))
                    {
                        throw new FormatException($"OID format: {oid}");
                    }
                    oid = oid.Substring("oid sha256:".Length);

                    Directory.CreateDirectory(Path.Combine(dest, oid.Substring(0, 2), oid.Substring(2, 2)));
                    var target = Path.Combine(dest, oid.Substring(0, 2), oid.Substring(2, 2), oid);
                    if (!File.Exists(target))
                    {
                        File.Copy(sources.Select(src => Path.Combine(src, oid.Substring(0, 2), oid.Substring(2, 2), oid)).Where(File.Exists).First(), target);
                        count++;
                    }
                }
                else if (entry.TargetType == TreeEntryTargetType.Tree)
                {
                    count += CopyLfsFiles((Tree)entry.Target, dest, sources);
                }
            }

            return count;
        }
    }
}
