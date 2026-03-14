using Blade.Source;

namespace Blade.Tests;

[TestFixture]
public class SourceTextTests
{
    [Test]
    public void GetLocation_HandlesLfCrLfAndCrLineBreaks()
    {
        SourceText source = new("ab\ncd\r\nef\rg", "sample.blade");

        Assert.That(source.GetLocation(0), Is.EqualTo(new SourceLocation("sample.blade", 1, 1)));
        Assert.That(source.GetLocation(1), Is.EqualTo(new SourceLocation("sample.blade", 1, 2)));
        Assert.That(source.GetLocation(3), Is.EqualTo(new SourceLocation("sample.blade", 2, 1)));
        Assert.That(source.GetLocation(6), Is.EqualTo(new SourceLocation("sample.blade", 2, 4)));
        Assert.That(source.GetLocation(7), Is.EqualTo(new SourceLocation("sample.blade", 3, 1)));
        Assert.That(source.GetLocation(10), Is.EqualTo(new SourceLocation("sample.blade", 4, 1)));
    }

    [Test]
    public void GetLocation_ReusesCachedLineStarts()
    {
        SourceText source = new("first\nsecond\nthird");

        SourceLocation first = source.GetLocation(8);
        SourceLocation second = source.GetLocation(14);

        Assert.That(first, Is.EqualTo(new SourceLocation("<input>", 2, 3)));
        Assert.That(second, Is.EqualTo(new SourceLocation("<input>", 3, 2)));
    }

    [Test]
    public void ToStringAndAsSpan_ReturnExpectedSlices()
    {
        SourceText source = new("0123456789");
        TextSpan span = new(2, 4);

        Assert.That(source.ToString(span), Is.EqualTo("2345"));
        Assert.That(source.AsSpan(span).ToString(), Is.EqualTo("2345"));
        Assert.That(source.Length, Is.EqualTo(10));
        Assert.That(source[6], Is.EqualTo('6'));
        Assert.That(source.FilePath, Is.EqualTo("<input>"));
    }

    [Test]
    public void GetLocation_HandlesCarriageReturnAtEndOfText()
    {
        SourceText source = new("a\r", "tail.blade");

        Assert.That(source.GetLocation(1), Is.EqualTo(new SourceLocation("tail.blade", 1, 2)));
    }
}
