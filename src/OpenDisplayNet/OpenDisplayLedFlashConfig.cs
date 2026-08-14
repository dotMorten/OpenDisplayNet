namespace OpenDisplayNet;

/// <summary>One step in an OpenDisplay LED flash pattern.</summary>
public sealed record OpenDisplayLedFlashStep(
    byte Color,
    byte FlashCount = 0,
    byte LoopDelayUnits = 0,
    byte InterDelayUnits = 0)
{
    internal void Validate()
    {
        if (FlashCount > 0x0F)
        {
            throw new ArgumentOutOfRangeException(nameof(FlashCount));
        }

        if (LoopDelayUnits > 0x0F)
        {
            throw new ArgumentOutOfRangeException(nameof(LoopDelayUnits));
        }
    }
}

/// <summary>Typed payload for the OpenDisplay LED activate command.</summary>
public sealed record OpenDisplayLedFlashConfig(
    byte Mode,
    byte Brightness,
    OpenDisplayLedFlashStep Step1,
    OpenDisplayLedFlashStep? Step2 = null,
    OpenDisplayLedFlashStep? Step3 = null,
    byte? GroupRepeats = 1,
    byte Reserved = 0)
{
    /// <summary>Creates a one-step LED flash pattern.</summary>
    public static OpenDisplayLedFlashConfig Single(
        byte color,
        byte flashCount = 1,
        byte loopDelayUnits = 0,
        byte interDelayUnits = 0,
        byte brightness = 8,
        byte? groupRepeats = 1)
        => new(1, brightness, new(color, flashCount, loopDelayUnits, interDelayUnits), GroupRepeats: groupRepeats);

    /// <summary>Serializes this configuration to the firmware's fixed 12-byte payload.</summary>
    public byte[] ToBytes()
    {
        if (Mode > 0x0F)
        {
            throw new ArgumentOutOfRangeException(nameof(Mode));
        }

        if (Brightness is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(Brightness));
        }

        if (GroupRepeats is 0 or 255)
        {
            throw new ArgumentOutOfRangeException(nameof(GroupRepeats), "Group repeats must be 1 to 254, or null for infinite.");
        }

        Step1.Validate();
        (Step2 ?? new OpenDisplayLedFlashStep(0)).Validate();
        (Step3 ?? new OpenDisplayLedFlashStep(0)).Validate();

        byte[] result = new byte[12];
        result[0] = (byte)(((Brightness - 1) << 4) | Mode);
        WriteStep(result, 1, Step1);
        WriteStep(result, 4, Step2 ?? new OpenDisplayLedFlashStep(0));
        WriteStep(result, 7, Step3 ?? new OpenDisplayLedFlashStep(0));
        result[10] = GroupRepeats is null ? (byte)0xFE : (byte)(GroupRepeats.Value - 1);
        result[11] = Reserved;
        return result;
    }

    private static void WriteStep(byte[] target, int offset, OpenDisplayLedFlashStep step)
    {
        target[offset] = step.Color;
        target[offset + 1] = (byte)((step.LoopDelayUnits << 4) | step.FlashCount);
        target[offset + 2] = step.InterDelayUnits;
    }
}
