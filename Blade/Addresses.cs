using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;

namespace Blade;


public enum AddressSpace
{
    /// <summary>
    /// The cog register address space.
    ///
    /// Addresses point to the COG's 512 32-bit registers, which are directly accessible by instructions and can be used for data storage or as instruction targets for jumps and calls.
    /// </summary>
    Cog,
    /// <summary>
    /// The LUT (Look-Up Table) address space.
    ///
    /// Addresses point to the LUT's 512 32-bit registers, which accessible through `RDLUT` and `WRLUT` instructions and can be used for data storage or as instruction targets for jumps and calls.
    /// </summary>
    Lut,

    /// <summary>
    /// The hub address space.
    ///
    /// Addresses point to bytes in the shared hub memory, which is accessible by all COGs.
    /// </summary>
    Hub,
}

public static class AddressSpaceExtensions
{
    /// <summary>
    /// Gets the size of an address unit in bytes for this address space, which is the smallest addressable unit of memory for this address space.
    /// </summary>
    public static int GetAddressUnitSizeInBytes(this AddressSpace space) => space switch
    {
        AddressSpace.Cog => 4,
        AddressSpace.Lut => 4,
        AddressSpace.Hub => 1,
        _ => Assert.UnreachableValue<int>($"Unsupported address space '{space}'.") // pragma: force-coverage
    };

    /// <summary>
    /// Gets the size of an address unit in bits for this address space, which is the smallest addressable unit of memory for this address space.
    /// </summary>
    public static int GetAddressUnitSizeInBits(this AddressSpace space) => 8 * GetAddressUnitSizeInBytes(space);

    /// <summary>
    /// Returns the number of addressable units in this address space.
    /// </summary>
    /// <param name="space"></param>
    /// <returns></returns>
    public static int GetAddressUnitCount(this AddressSpace space) => space switch
    {
        AddressSpace.Cog => 512,
        AddressSpace.Lut => 512,
        AddressSpace.Hub => 512 * 1024,
        _ => Assert.UnreachableValue<int>($"Unsupported address space '{space}'.") // pragma: force-coverage
    };

    /// <summary>
    /// Returns the total size of this address space in bytes.
    /// </summary>
    public static int GetAddressSpaceSizeInBytes(this AddressSpace space) => space.GetAddressUnitSizeInBytes() * space.GetAddressUnitCount();
}

/// <summary>
/// Generic interface over virtual memory addresses of a Propeller 2.
/// </summary>
public interface IVirtualAddress
{
    /// <summary>
    /// The address space of this address, which determines how the address is accessed and interpreted.
    /// </summary>
    AddressSpace AddressSpace { get; }

    /// <summary>
    /// Returns the address which PC will be set to when this address is used as a jump target, validating that this address is a legal jump target.
    /// </summary>
    /// <returns>The PC value for this address or <c>null</c> if not a valid jump target.</returns>
    /// <remarks>
    /// The returned value is only valid for the returned address space and must not be used with another address space.
    /// </remarks>
    (AddressSpace space, int address)? GetJumpTarget();

    /// <summary>
    /// Returns the address to use when accessing this address as data.
    /// </summary>
    /// <remarks>
    /// The returned value is only valid for the returned address space and must not be used with another address space.
    /// </remarks>
    (AddressSpace space, int address) GetDataAddress();
}


public static class IVirtualAddressExtensions
{
    /// <summary>
    /// Tries to interpret a virtual address as a concrete COG address.
    /// </summary>
    public static bool TryGetCogAddress(this IVirtualAddress address, out CogAddress cogAddress)
    {
        (var space, var absolute) = Requires.NotNull(address).GetDataAddress();
        if (space == AddressSpace.Cog)
        {
            cogAddress = new CogAddress(absolute);
            return true;
        }
        cogAddress = CogAddress.Zero;
        return false;
    }

    /// <summary>
    /// Converts a virtual address to a concrete COG address.
    /// </summary>
    public static CogAddress ToCogAddress(this IVirtualAddress address)
    {
        if (TryGetCogAddress(address, out var cogAddress))
            return cogAddress;
        throw new ArgumentException($"Virtual address {address} is not in the cog address space and cannot be converted to a cog address.");
    }

    /// <summary>
    /// Tries to interpret a virtual address as a concrete LUT address.
    /// </summary>
    public static bool TryGetLutAddress(this IVirtualAddress address, out LutAddress lutAddress)
    {
        (var space, var absolute) = Requires.NotNull(address).GetDataAddress();
        if (space == AddressSpace.Lut)
        {
            lutAddress = new LutAddress(absolute);
            return true;
        }
        lutAddress = LutAddress.Zero;
        return false;
    }

    /// <summary>
    /// Converts a virtual address to a concrete LUT address.
    /// </summary>
    public static LutAddress ToLutAddress(this IVirtualAddress address)
    {
        if (TryGetLutAddress(address, out var lutAddress))
            return lutAddress;
        throw new ArgumentException($"Virtual address {address} is not in the LUT address space and cannot be converted to a LUT address.");
    }

    /// <summary>
    /// Tries to interpret a virtual address as a concrete hub address.
    /// </summary>
    public static bool TryGetHubAddress(this IVirtualAddress address, out HubAddress hubAddress)
    {
        (var space, var absolute) = Requires.NotNull(address).GetDataAddress();
        if (space == AddressSpace.Hub)
        {
            hubAddress = new HubAddress(absolute);
            return true;
        }
        hubAddress = HubAddress.Zero;
        return false;
    }

    /// <summary>
    /// Converts a virtual address to a concrete hub address.
    /// </summary>
    public static HubAddress ToHubAddress(this IVirtualAddress address)
    {
        if (TryGetHubAddress(address, out var hubAddress))
            return hubAddress;
        throw new ArgumentException($"Virtual address {address} is not in the hub address space and cannot be converted to a hub address.");
    }
}

/// <summary>
/// An address in the Cog address space.
/// </summary>
[SuppressMessage("Usage", "CA2225:Operator overloads have named alternates", Justification = "Address value types use operators and conversions as their canonical API.")]
public readonly record struct CogAddress : IVirtualAddress, IComparable<CogAddress>, IEquatable<CogAddress>, IComparisonOperators<CogAddress, CogAddress, bool>, IEqualityOperators<CogAddress, CogAddress, bool>, IAdditionOperators<CogAddress, int, CogAddress>, ISubtractionOperators<CogAddress, int, CogAddress>, IFormattable
{
    public static CogAddress Zero { get; } = new(0);

    private readonly int address;

    public CogAddress(int address)
    {
        Requires.That(address >= 0x000 && address <= 0x1FF, $"COG address must be between $000 and $1FF, but got ${address:X5}.");
        this.address = address;
    }

    public AddressSpace AddressSpace => AddressSpace.Cog;

    public override int GetHashCode() => HashCode.Combine("CogAddress", address);
    public override string ToString() => ToString(format: null, CultureInfo.InvariantCulture);

    public int CompareTo(CogAddress other) => address.CompareTo(other.address);

    public (AddressSpace space, int address)? GetJumpTarget() => (AddressSpace.Cog, address);

    public (AddressSpace space, int address) GetDataAddress() => (AddressSpace.Cog, address);

    // The operators always throw on overflow, since addresses outside of the valid range are always invalid and thus should not be allowed:
    public static CogAddress operator +(CogAddress left, int right) => new(checked(left.address + right));
    public static CogAddress operator -(CogAddress left, int right) => new(checked(left.address - right));
    public static bool operator <(CogAddress left, CogAddress right) => left.address < right.address;
    public static bool operator <=(CogAddress left, CogAddress right) => left.address <= right.address;
    public static bool operator >(CogAddress left, CogAddress right) => left.address > right.address;
    public static bool operator >=(CogAddress left, CogAddress right) => left.address >= right.address;

    public static explicit operator int(CogAddress cogAddress) => cogAddress.address;

    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        string effectiveFormat = string.IsNullOrWhiteSpace(format) ? "X3" : format;
        IFormatProvider provider = formatProvider ?? CultureInfo.InvariantCulture;
        return $"${address.ToString(effectiveFormat, provider)}";
    }
}

/// <summary>
/// An address in the LUT address space.
/// </summary>
[SuppressMessage("Usage", "CA2225:Operator overloads have named alternates", Justification = "Address value types use operators and conversions as their canonical API.")]
public readonly record struct LutAddress : IVirtualAddress, IComparable<LutAddress>, IEquatable<LutAddress>, IComparisonOperators<LutAddress, LutAddress, bool>, IEqualityOperators<LutAddress, LutAddress, bool>, IAdditionOperators<LutAddress, int, LutAddress>, ISubtractionOperators<LutAddress, int, LutAddress>, IFormattable
{
    public static LutAddress Zero { get; } = new(0);

    private readonly int address;

    public LutAddress(int address)
    {
        Requires.That(address >= 0x000 && address <= 0x1FF, $"LUT address must be between $000 and $1FF, but got ${address:X5}.");
        this.address = address;
    }

    public AddressSpace AddressSpace => AddressSpace.Lut;

    public override int GetHashCode() => HashCode.Combine("LutAddress", address);
    public override string ToString() => ToString(format: null, CultureInfo.InvariantCulture);

    public int CompareTo(LutAddress other) => address.CompareTo(other.address);

    public (AddressSpace space, int address)? GetJumpTarget() => (AddressSpace.Lut, checked(address + 0x200));

    public (AddressSpace space, int address) GetDataAddress() => (AddressSpace.Lut, address);

    public static LutAddress operator +(LutAddress left, int right) => new(checked(left.address + right));
    public static LutAddress operator -(LutAddress left, int right) => new(checked(left.address - right));
    public static bool operator <(LutAddress left, LutAddress right) => left.address < right.address;
    public static bool operator <=(LutAddress left, LutAddress right) => left.address <= right.address;
    public static bool operator >(LutAddress left, LutAddress right) => left.address > right.address;
    public static bool operator >=(LutAddress left, LutAddress right) => left.address >= right.address;

    public static explicit operator int(LutAddress lutAddress) => lutAddress.address;

    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        string effectiveFormat = string.IsNullOrWhiteSpace(format) ? "X3" : format;
        IFormatProvider provider = formatProvider ?? CultureInfo.InvariantCulture;
        return $"${address.ToString(effectiveFormat, provider)}";
    }
}

[SuppressMessage("Usage", "CA2225:Operator overloads have named alternates", Justification = "Address value types use operators and conversions as their canonical API.")]
public readonly record struct HubAddress : IVirtualAddress, IComparable<HubAddress>, IEquatable<HubAddress>, IComparisonOperators<HubAddress, HubAddress, bool>, IEqualityOperators<HubAddress, HubAddress, bool>, IAdditionOperators<HubAddress, int, HubAddress>, ISubtractionOperators<HubAddress, int, HubAddress>, IFormattable
{
    public static HubAddress Zero { get; } = new(0);

    private readonly int address;

    public HubAddress(int address)
    {
        Requires.That(address >= 0x00000 && address <= 0x1FFFF, $"Hub address must be between $00000 and $1FFFF, but got ${address:X5}.");
        this.address = address;
    }

    public AddressSpace AddressSpace => AddressSpace.Hub;

    public override int GetHashCode() => HashCode.Combine("HubAddress", address);
    public override string ToString() => ToString(format: null, CultureInfo.InvariantCulture);

    public int CompareTo(HubAddress other) => address.CompareTo(other.address);

    public (AddressSpace space, int address)? GetJumpTarget()
    {
        if (address < 0x400)
            return null;

        return (AddressSpace.Hub, address);
    }

    public (AddressSpace space, int address) GetDataAddress() => (AddressSpace.Hub, address);

    public static HubAddress operator +(HubAddress left, int right) => new(checked(left.address + right));
    public static HubAddress operator -(HubAddress left, int right) => new(checked(left.address - right));
    public static bool operator <(HubAddress left, HubAddress right) => left.address < right.address;
    public static bool operator <=(HubAddress left, HubAddress right) => left.address <= right.address;
    public static bool operator >(HubAddress left, HubAddress right) => left.address > right.address;
    public static bool operator >=(HubAddress left, HubAddress right) => left.address >= right.address;

    public static explicit operator int(HubAddress hubAddress) => hubAddress.address;

    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        string effectiveFormat = string.IsNullOrWhiteSpace(format) ? "X5" : format;
        IFormatProvider provider = formatProvider ?? CultureInfo.InvariantCulture;
        return $"${address.ToString(effectiveFormat, provider)}";
    }
}

[SuppressMessage("Usage", "CA2225:Operator overloads have named alternates", Justification = "Address value types use operators and conversions as their canonical API.")]
public readonly record struct VirtualAddress : IVirtualAddress, IEquatable<VirtualAddress>
{
    private readonly int address;

    public VirtualAddress(AddressSpace addressSpace, int address)
    {
        this.AddressSpace = addressSpace;
        this.address = addressSpace switch
        {
            AddressSpace.Cog => Requires.InRange(address, 0x000, 0x1FF, $"COG register address must be between $000 and $1FF, but got ${address:X3}."),
            AddressSpace.Lut => Requires.InRange(address, 0x000, 0x1FF, $"LUT address must be between $000 and $1FF, but got ${address:X3}."),
            AddressSpace.Hub => Requires.InRange(address, 0x00000, 0x1FFFF, $"Hub address must be between $00000 and $1FFFF, but got ${address:X5}."),
            _ => Assert.UnreachableValue<int>($"Unsupported address space '{addressSpace}'.")
        };
    }

    public VirtualAddress(CogAddress address) : this(AddressSpace.Cog, (int)address) { }
    public VirtualAddress(LutAddress address) : this(AddressSpace.Lut, (int)address) { }
    public VirtualAddress(HubAddress address) : this(AddressSpace.Hub, (int)address) { }

    public bool IsValidJumpTarget => this.AddressSpace switch
    {
        AddressSpace.Cog => true,
        AddressSpace.Lut => true,
        AddressSpace.Hub => (this.address >= 0x400), // Code cannot jump to hub execution below $400.
        _ => Assert.UnreachableValue<bool>($"Unsupported address space '{this.AddressSpace}'.")
    };

    /// <summary>
    /// Gets the actual address to encode for a jump instruction targeting this virtual address, validating that it's a legal jump target.
    /// </summary>
    /// <returns>The address to use for a jump instruction.</returns>
    public readonly (AddressSpace, int)? GetJumpTarget()
    {
        return this.AddressSpace switch
        {
            AddressSpace.Cog => (AddressSpace.Cog, this.address), // COG register PC are mapped between 0x000 and 0x1FF
            AddressSpace.Lut => (AddressSpace.Lut, this.address + 0x200), // LUT PCs are mapped between 0x200 and 0x3FF
            AddressSpace.Hub => this.address >= 0x400 ? (AddressSpace.Hub, this.address) : null, // Hub execution addresses are mapped directly to their hub address, but are shadowed by cog and lut execution.
            _ => Assert.UnreachableValue<(AddressSpace, int)>($"Unsupported address space '{this.AddressSpace}'.")
        };
    }

    public readonly (AddressSpace, int) GetDataAddress() => (this.AddressSpace, this.address);

    public AddressSpace AddressSpace { get; }

    public static implicit operator VirtualAddress(CogAddress cogAddress) => new(cogAddress);
    public static implicit operator VirtualAddress(LutAddress lutAddress) => new(lutAddress);
    public static implicit operator VirtualAddress(HubAddress hubAddress) => new(hubAddress);

    public override string ToString()
    {
        return this.AddressSpace switch
        {
            AddressSpace.Cog => new CogAddress(address).ToString(),
            AddressSpace.Lut => new LutAddress(address).ToString(),
            AddressSpace.Hub => new HubAddress(address).ToString(),
            _ => Assert.UnreachableValue<string>($"Unsupported address space '{AddressSpace}'.") // pragma: force-coverage
        };
    }
}

/// <summary>
/// An address placed in memory, having both virtual and physical placement.
/// </summary>
public readonly record struct MemoryAddress : IVirtualAddress, IEquatable<MemoryAddress>
{
    public MemoryAddress(HubAddress hubAddress, VirtualAddress virtualAddress)
    {
        this.Physical = hubAddress;
        this.Virtual = virtualAddress;

        if (virtualAddress.AddressSpace == AddressSpace.Hub)
        {
            // Ensure that a virtual hub address is always consistent with its physical hub address,
            // since the virtual-to-physical mapping for hub addresses is identity.
            Requires.That(hubAddress == virtualAddress.ToHubAddress());
        }
    }

    public HubAddress Physical { get; }

    public VirtualAddress Virtual { get; }

    public AddressSpace AddressSpace => this.Virtual.AddressSpace;

    public (AddressSpace space, int address)? GetJumpTarget() => Virtual.GetJumpTarget();

    public (AddressSpace space, int address) GetDataAddress() => Virtual.GetDataAddress();

    public override string ToString() => $"{Virtual} @{Physical}";
}
