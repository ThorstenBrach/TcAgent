// Folders shared with the agent (SharedFolders.cs): what is refused, the list per solution, kept files, the backup.
//
// SPDX-License-Identifier: MIT
using Xunit;

namespace TcAgent.Tests;

[Collection("knowledge-layers")]
public class SharedFoldersTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "tcagent-shared-" + Guid.NewGuid().ToString("N"));
    readonly string old = KnowledgeLayers.UserRootOverride;
    string Sub(string name) => Directory.CreateDirectory(Path.Combine(dir, name)).FullName;

    public SharedFoldersTests() { Directory.CreateDirectory(dir); KnowledgeLayers.UserRootOverride = Sub("user"); SharedFolders.ProfileOverride = @"C:\Users\me"; }
    public void Dispose() { KnowledgeLayers.UserRootOverride = old; SharedFolders.ProfileOverride = null; try { Directory.Delete(dir, true); } catch { } }

    [Theory]
    [InlineData(@"C:\", "drive")]
    [InlineData(@"D:\", "drive")]
    [InlineData(@"C:\Users\me", "profile")]
    [InlineData(@"C:\Users", "profile")]
    [InlineData(@"C:\Users\me\AppData\Local\Other", "AppData")]
    [InlineData(@"C:\Users\me\.ssh", ".ssh")]
    public void Too_much_is_refused_with_the_reason(string path, string word) =>
        Assert.Contains(word, SharedFolders.Refused(path, @"C:\Users\me"));

    [Theory]
    [InlineData(@"D:\Docs\Machine")]
    [InlineData(@"C:\Users\me\Documents\Manuals")]
    [InlineData(@"C:\Users\mex\Docs")]
    public void Normal_folders_are_fine(string path) => Assert.Null(SharedFolders.Refused(path, @"C:\Users\me"));

    [Fact]
    public void System_folders_are_refused() =>
        Assert.Contains("System", SharedFolders.Refused(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));

    [Fact]
    public void A_folder_for_a_solution_counts_only_there()
    {
        var a = Sub("a"); var b = Sub("b");
        SharedFolders.Add(a, @"D:\Projects\One");
        SharedFolders.Add(b, null);
        Assert.Equal(2, SharedFolders.Active(@"d:\projects\one\").Count);
        Assert.Equal(b, SharedFolders.Active(@"D:\Projects\Two").Single().Path);
        Assert.Equal(b, SharedFolders.Active(null).Single().Path);
    }

    [Fact]
    public void A_folder_is_listed_once_and_can_be_removed()
    {
        var a = Sub("a");
        SharedFolders.Add(a, @"D:\One");
        SharedFolders.Add(a + "\\", null);
        Assert.Null(SharedFolders.Load().Single().Solution);
        Assert.True(SharedFolders.Remove(a));
        Assert.False(SharedFolders.Remove(a));
        Assert.Empty(SharedFolders.Load());
    }

    [Fact]
    public void A_missing_folder_is_not_added() =>
        Assert.Throws<InvalidOperationException>(() => SharedFolders.Add(Path.Combine(dir, "nothing"), null));

    [Fact]
    public void A_kept_file_is_a_copy_in_a_shared_folder()
    {
        // the folder of the copies lies below AppData in real life, which is refused for everything else
        KnowledgeLayers.UserRootOverride = Directory.CreateDirectory(Path.Combine(dir, "AppData", "TcAgent")).FullName;
        SharedFolders.ProfileOverride = dir;
        var f = Path.Combine(Sub("src"), "manual.pdf");
        File.WriteAllText(f, "x");
        var copy = SharedFolders.KeepFile(f, @"D:\One");
        Assert.True(File.Exists(copy));
        Assert.True(File.Exists(f));
        Assert.Equal(Path.GetDirectoryName(copy), SharedFolders.Active(@"D:\One").Single().Path);
        Assert.Empty(SharedFolders.Active(@"D:\Two"));
        SharedFolders.KeepFile(f, @"D:\One");
        Assert.Single(SharedFolders.Load());
    }

    [Fact]
    public void The_list_and_kept_files_belong_to_the_backup()
    {
        Assert.Contains(SharedFolders.ListName, KnowledgeBackup.Parts);
        Assert.Contains(SharedFolders.FilesName, KnowledgeBackup.Parts);
    }
}
