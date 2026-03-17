using System;

namespace Blade.Tests;

[TestFixture]
public class RequiresTests
{
    [Test]
    public void NotNull_ReturnsSameInstance()
    {
        object value = new();
        object result = Requires.NotNull(value);
        Assert.That(result, Is.SameAs(value));
    }

    [Test]
    public void NotNull_ThrowsForNull()
    {
        Assert.Throws<ArgumentNullException>(() => Requires.NotNull<object>(null));
    }

    [Test]
    public void NotNullOrWhiteSpace_ValidatesInput()
    {
        Assert.That(Requires.NotNullOrWhiteSpace("blade"), Is.EqualTo("blade"));
        Assert.Throws<ArgumentNullException>(() => Requires.NotNullOrWhiteSpace(null));
        Assert.Throws<ArgumentException>(() => Requires.NotNullOrWhiteSpace(""));
        Assert.Throws<ArgumentException>(() => Requires.NotNullOrWhiteSpace("   "));
    }

    [Test]
    public void NonNegative_ValidatesRange()
    {
        Assert.That(Requires.NonNegative(0), Is.EqualTo(0));
        Assert.That(Requires.NonNegative(5), Is.EqualTo(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => Requires.NonNegative(-1));
    }

    [Test]
    public void Positive_ValidatesRange()
    {
        Assert.That(Requires.Positive(1), Is.EqualTo(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Requires.Positive(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Requires.Positive(-2));
    }

    [Test]
    public void InRange_ValidatesBounds()
    {
        Assert.That(Requires.InRange(3, 1, 5), Is.EqualTo(3));
        Assert.That(Requires.InRange(1, 1, 5), Is.EqualTo(1));
        Assert.That(Requires.InRange(5, 1, 5), Is.EqualTo(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => Requires.InRange(0, 1, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => Requires.InRange(6, 1, 5));
    }
}
