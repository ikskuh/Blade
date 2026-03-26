namespace Blade;

/// <summary>
/// Enumeration of all Propeller 2 special registers.
/// </summary>
public enum P2SpecialRegister
{
    /// <summary>No special register.</summary>
    None = 0,

    /// <summary>INT3 call address.</summary>
    IJMP3 = 0x1F0,

    /// <summary>INT3 return address.</summary>
    IRET3 = 0x1F1,

    /// <summary>INT2 call address.</summary>
    IJMP2 = 0x1F2,

    /// <summary>INT2 return address.</summary>
    IRET2 = 0x1F3,

    /// <summary>INT1 call address.</summary>
    IJMP1 = 0x1F4,

    /// <summary>INT1 return address.</summary>
    IRET1 = 0x1F5,

    /// <summary>Used with CALLPA, CALLD and LOC.</summary>
    PA = 0x1F6,

    /// <summary>Used with CALLPB, CALLD and LOC.</summary>
    PB = 0x1F7,

    /// <summary>Pointer A register.</summary>
    PTRA = 0x1F8,

    /// <summary>Pointer B register.</summary>
    PTRB = 0x1F9,

    /// <summary>I/O port A direction register.</summary>
    DIRA = 0x1FA,

    /// <summary>I/O port B direction register.</summary>
    DIRB = 0x1FB,

    /// <summary>I/O port A output register.</summary>
    OUTA = 0x1FC,

    /// <summary>I/O port B output register.</summary>
    OUTB = 0x1FD,

    /// <summary>I/O port A input register.</summary>
    INA = 0x1FE,

    /// <summary>I/O port B input register.</summary>
    INB = 0x1FF,
}
