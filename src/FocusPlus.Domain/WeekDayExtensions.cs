namespace FocusPlus.Domain;

public static class WeekDayExtensions
{
    private static readonly WeekDays[] Ordered =
    [
        WeekDays.Sunday,
        WeekDays.Monday,
        WeekDays.Tuesday,
        WeekDays.Wednesday,
        WeekDays.Thursday,
        WeekDays.Friday,
        WeekDays.Saturday
    ];

    /// <summary>Converts a <see cref="System.DayOfWeek"/> to its <see cref="WeekDays"/> flag.</summary>
    public static WeekDays ToFlag(this DayOfWeek day) => Ordered[(int)day];

    public static bool Includes(this WeekDays mask, DayOfWeek day) => (mask & day.ToFlag()) != 0;

    public static bool Includes(this WeekDays mask, DateTime date) => mask.Includes(date.DayOfWeek);

    /// <summary>Human readable ordered list of the flags, using invariant English short names.</summary>
    public static IReadOnlyList<DayOfWeek> ToDays(this WeekDays mask) =>
        Ordered.Where(f => (mask & f) != 0).Select(f => (DayOfWeek)Array.IndexOf(Ordered, f)).ToList();

    public static WeekDays FromDays(IEnumerable<DayOfWeek> days)
    {
        WeekDays result = WeekDays.None;
        foreach (var day in days)
        {
            result |= day.ToFlag();
        }

        return result;
    }

    public static string ToShortList(this WeekDays mask)
    {
        var names = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        var selected = Ordered.Where(f => (mask & f) != 0).Select(f => names[Array.IndexOf(Ordered, f)]);
        return string.Join(", ", selected);
    }
}
