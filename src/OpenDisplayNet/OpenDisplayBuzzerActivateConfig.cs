namespace OpenDisplayNet;

/// <summary>A single frequency/duration pair in an OpenDisplay buzzer pattern.</summary>
public sealed record OpenDisplayBuzzerStep(byte FrequencyIndex, byte DurationUnits);

/// <summary>A sequence of buzzer steps played in order.</summary>
public sealed record OpenDisplayBuzzerPattern(IReadOnlyList<OpenDisplayBuzzerStep> Steps)
{
    internal byte[] ToBytes()
    {
        if (Steps.Count is < 1 or > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(Steps), "A buzzer pattern must contain 1 to 255 steps.");
        }

        byte[] result = new byte[1 + Steps.Count * 2];
        result[0] = (byte)Steps.Count;
        for (int index = 0; index < Steps.Count; index++)
        {
            result[1 + index * 2] = Steps[index].FrequencyIndex;
            result[2 + index * 2] = Steps[index].DurationUnits;
        }

        return result;
    }
}

/// <summary>Typed payload for the OpenDisplay buzzer activate command.</summary>
public sealed record OpenDisplayBuzzerActivateConfig(byte OuterRepeats, IReadOnlyList<OpenDisplayBuzzerPattern> Patterns)
{
    /// <summary>Creates a simple one-tone buzzer configuration.</summary>
    public static OpenDisplayBuzzerActivateConfig Single(byte frequencyIndex, byte durationUnits, byte repeats = 1)
        => new(repeats, [new OpenDisplayBuzzerPattern([new OpenDisplayBuzzerStep(frequencyIndex, durationUnits)])]);

    /// <summary>Serializes this configuration to the firmware wire format.</summary>
    public byte[] ToBytes()
    {
        if (OuterRepeats == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(OuterRepeats));
        }

        if (Patterns.Count is < 1 or > byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(Patterns), "A buzzer activation must contain 1 to 255 patterns.");
        }

        byte[][] encodedPatterns = Patterns.Select(pattern => pattern.ToBytes()).ToArray();
        byte[] result = new byte[2 + encodedPatterns.Sum(pattern => pattern.Length)];
        result[0] = OuterRepeats;
        result[1] = (byte)Patterns.Count;
        int offset = 2;
        foreach (byte[] pattern in encodedPatterns)
        {
            pattern.CopyTo(result, offset);
            offset += pattern.Length;
        }

        return result;
    }
}
