using System;
using System.Linq;
using System.Reflection;

namespace Blade.Tests;

[TestFixture]
public class AddressTests
{
    [Test]
    public void AddressSpaceExtensions_ReportUnitSizesAndCapacity()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AddressSpace.Cog.GetAddressUnitSizeInBytes(), Is.EqualTo(4));
            Assert.That(AddressSpace.Lut.GetAddressUnitSizeInBits(), Is.EqualTo(32));
            Assert.That(AddressSpace.Cog.GetAddressUnitCount(), Is.EqualTo(0x200));
            Assert.That(AddressSpace.Lut.GetAddressSpaceSizeInBytes(), Is.EqualTo(0x800));
            Assert.That(AddressSpace.Hub.GetAddressSpaceSizeInBytes(), Is.EqualTo(0x80000));
        });
    }

    [Test]
    public void ConcreteAddressTypes_ValidateRangesAndFormatting()
    {
        Assert.Multiple(() =>
        {
            Assert.That(new CogAddress(0x1EF).ToString(), Is.EqualTo("$1EF"));
            Assert.That(new LutAddress(0x100).ToString(), Is.EqualTo("$100"));
            Assert.That(new HubAddress(0x1234).ToString(), Is.EqualTo("$01234"));
            Assert.That(() => _ = new CogAddress(0x200), Throws.TypeOf<ArgumentException>());
            Assert.That(() => _ = new LutAddress(-1), Throws.TypeOf<ArgumentException>());
            Assert.That(() => _ = new HubAddress(0x20000), Throws.TypeOf<ArgumentException>());
        });
    }

    [Test]
    public void ConcreteAddressTypes_SupportTypedArithmeticAndComparison()
    {
        CogAddress cog = new(0x10);
        LutAddress lut = new(0x20);
        HubAddress hub = new(0x400);

        Assert.Multiple(() =>
        {
            Assert.That(cog + 2, Is.EqualTo(new CogAddress(0x12)));
            Assert.That(lut - 1, Is.EqualTo(new LutAddress(0x1F)));
            Assert.That(hub + 4, Is.EqualTo(new HubAddress(0x404)));
            Assert.That(new CogAddress(0x10) < new CogAddress(0x11), Is.True);
            Assert.That(new HubAddress(0x400) >= new HubAddress(0x400), Is.True);
            Assert.That(() => _ = new CogAddress(0x1FF) + 1, Throws.TypeOf<ArgumentException>());
        });
    }

    [Test]
    public void IVirtualAddressExtensions_ConvertOnlyMatchingSpaces()
    {
        IVirtualAddress cog = new VirtualAddress(new CogAddress(0x12));
        IVirtualAddress lut = new MemoryAddress(new HubAddress(0x800), new VirtualAddress(new LutAddress(0x34)));
        IVirtualAddress hub = new HubAddress(0x456);

        Assert.Multiple(() =>
        {
            Assert.That(cog.TryGetCogAddress(out CogAddress cogAddress), Is.True);
            Assert.That(cogAddress, Is.EqualTo(new CogAddress(0x12)));
            Assert.That(cog.TryGetHubAddress(out _), Is.False);
            Assert.That(() => _ = cog.ToHubAddress(), Throws.TypeOf<ArgumentException>());

            Assert.That(lut.TryGetLutAddress(out LutAddress lutAddress), Is.True);
            Assert.That(lutAddress, Is.EqualTo(new LutAddress(0x34)));
            Assert.That(() => _ = lut.ToCogAddress(), Throws.TypeOf<ArgumentException>());

            Assert.That(hub.TryGetHubAddress(out HubAddress hubAddress), Is.True);
            Assert.That(hubAddress, Is.EqualTo(new HubAddress(0x456)));
            Assert.That(() => _ = hub.ToLutAddress(), Throws.TypeOf<ArgumentException>());
        });
    }

    [Test]
    public void VirtualAddress_ReportsJumpTargetsPerAddressSpace()
    {
        Assert.Multiple(() =>
        {
            Assert.That(new VirtualAddress(new CogAddress(0x12)).GetJumpTarget(), Is.EqualTo((AddressSpace.Cog, 0x12)));
            Assert.That(new VirtualAddress(new LutAddress(0x12)).GetJumpTarget(), Is.EqualTo((AddressSpace.Lut, 0x212)));
            Assert.That(new VirtualAddress(new HubAddress(0x3FF)).GetJumpTarget(), Is.Null);
            Assert.That(new VirtualAddress(new HubAddress(0x400)).GetJumpTarget(), Is.EqualTo((AddressSpace.Hub, 0x400)));
        });
    }

    [Test]
    public void MemoryAddress_RequiresHubVirtualConsistency()
    {
        MemoryAddress placedCog = new(new HubAddress(0x800), new VirtualAddress(new CogAddress(0x10)));

        Assert.Multiple(() =>
        {
            Assert.That(placedCog.Physical, Is.EqualTo(new HubAddress(0x800)));
            Assert.That(placedCog.Virtual, Is.EqualTo(new VirtualAddress(new CogAddress(0x10))));
            Assert.That(placedCog.GetDataAddress(), Is.EqualTo((AddressSpace.Cog, 0x10)));
            Assert.That(() => _ = new MemoryAddress(new HubAddress(0x801), new VirtualAddress(new HubAddress(0x800))), Throws.TypeOf<ArgumentException>());
        });
    }

    [Test]
    public void VirtualAddress_DoesNotExposeArithmeticOperators()
    {
        MethodInfo[] operators = typeof(VirtualAddress)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(static method => method.Name is "op_Addition" or "op_Subtraction")
            .ToArray();

        Assert.That(operators, Is.Empty);
    }
}
