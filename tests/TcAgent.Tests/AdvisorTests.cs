// Unit tests: the record of the Advisor - advice with its order, ratings appended later, the hit rate per kind of task.
// SPDX-License-Identifier: MIT
using TcAgent;
using Xunit;

public class AdvisorTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "tcagent-advisor-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    static Advisor.Item Advice(string kind = "review") => new()
    {
        Kind = kind, Trigger = "nightly", Finding = "The timeout of the clamp is never reset",
        Evidence = { "FB_Clamp line 42: tTimeout(IN := bClose)" }, Order = "Fix the reset of the clamp timeout with the workflow bug-fix", Confidence = "High",
    };

    [Fact]
    public void AdviceIsKeptInTheProject()
    {
        var it = Advisor.Record(dir, Advice());
        Assert.StartsWith("AD-", it.Id);
        Assert.Equal("high", it.Confidence);
        Assert.True(Directory.GetFiles(Path.Combine(dir, ".tcagent", "advisor"), "*.jsonl").Length == 1);
        var back = Assert.Single(Advisor.Load(dir));
        Assert.Equal(it.Id, back.Id);
        Assert.Equal("FB_Clamp line 42: tTimeout(IN := bClose)", Assert.Single(back.Evidence));
    }

    [Theory]
    [InlineData("", "e", "o", "finding")]
    [InlineData("f", null, "o", "evidence")]
    [InlineData("f", "e", "", "order")]
    public void AdviceNeedsFindingEvidenceAndOrder(string finding, string evidence, string order, string field)
    {
        var it = new Advisor.Item { Finding = finding, Order = order };
        if (evidence != null) it.Evidence.Add(evidence);
        var ex = Assert.Throws<ArgumentException>(() => Advisor.Record(dir, it));
        Assert.StartsWith(field, ex.Message);
    }

    [Fact]
    public void TheLatestRatingCounts()
    {
        var it = Advisor.Record(dir, Advice());
        Advisor.Rate(dir, it.Id, "wrong", "anna", null);
        Advisor.Rate(dir, it.Id, "right", "ben", "after a second look");
        var back = Assert.Single(Advisor.Load(dir));
        Assert.Equal("right", back.Rating);
        Assert.Equal("ben", back.RatedBy);
        Assert.Equal("after a second look", back.Comment);
    }

    [Fact]
    public void AdviceIsActedOnDoneOrDeleted()
    {
        var a = Advisor.Record(dir, Advice());
        var b = Advisor.Record(dir, Advice());
        Assert.Equal("open", Advisor.Load(dir)[0].State);
        Advisor.Mark(dir, a.Id, "acted", "anna");
        Assert.Equal("acted", Advisor.Load(dir).Single(i => i.Id == a.Id).State);
        Advisor.Mark(dir, a.Id, "done", "anna", "fixed in commit 1a2b");
        var done = Advisor.Load(dir).Single(i => i.Id == a.Id);
        Assert.Equal("done", done.State);
        Assert.Equal("fixed in commit 1a2b", done.DoneComment);
        Advisor.Mark(dir, b.Id, "delete", "anna");
        Assert.Equal(new[] { a.Id }, Advisor.Load(dir).Select(i => i.Id));
        Assert.Throws<ArgumentException>(() => Advisor.Mark(dir, b.Id, "done", "anna"));
    }

    [Fact]
    public void AnUnknownRatingOrIdIsRefused()
    {
        var it = Advisor.Record(dir, Advice());
        Assert.Throws<ArgumentException>(() => Advisor.Rate(dir, it.Id, "great", "anna", null));
        Assert.Throws<ArgumentException>(() => Advisor.Rate(dir, "AD-00000000", "right", "anna", null));
    }

    [Fact]
    public void TheRateNeedsTenRatings()
    {
        var items = Enumerable.Range(0, 9).Select(i => new Advisor.Item { Kind = "review", Time = DateTime.Now, Rating = "right" }).ToList();
        Assert.Null(Assert.Single(Advisor.Quotas(items, DateTime.Now)).Rate);
        items.Add(new Advisor.Item { Kind = "review", Time = DateTime.Now, Rating = "wrong" });
        Assert.Equal(0.9, Assert.Single(Advisor.Quotas(items, DateTime.Now)).Rate);
    }

    [Fact]
    public void OnlyTheLastTwentyRatingsCount()
    {
        var now = DateTime.Now;
        var items = Enumerable.Range(0, 30).Select(i => new Advisor.Item { Kind = "diagnose", Time = now, RatedAt = now.AddMinutes(i), Rating = i < 10 ? "wrong" : "right" }).ToList();
        var q = Assert.Single(Advisor.Quotas(items, now));
        Assert.Equal(20, q.Rated);
        Assert.Equal(1.0, q.Rate);
    }

    [Fact]
    public void OldUnratedAdviceIsNotOpen()
    {
        var now = DateTime.Now;
        var items = new List<Advisor.Item> { new() { Kind = "review", Time = now.AddDays(-20) }, new() { Kind = "review", Time = now.AddDays(-1) } };
        Assert.Equal(1, Assert.Single(Advisor.Quotas(items, now)).Open);
    }

    [Fact]
    public void ABrokenLineHidesNothingElse()
    {
        var items = Advisor.Parse(new[] { "{\"id\":\"AD-1\",\"finding\":\"a\"}", "<<<<<<< HEAD", "{\"id\":\"AD-2\",\"finding\":\"b\"}" });
        Assert.Equal(new[] { "AD-1", "AD-2" }, items.Select(i => i.Id));
    }
}
