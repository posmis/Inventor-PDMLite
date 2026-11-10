namespace PdmLite.Core.Documents;

public sealed record ReplacementFailure<T>(T Item, Exception Error, bool? Changed);
public sealed record ReplacementBatchResult<T>(int Replaced, bool Changed, IReadOnlyList<ReplacementFailure<T>> Failures);

/// <summary>Успешные замены и подтверждённые частичные изменения отделены от неудачных попыток.</summary>
public static class ReplacementBatch
{
    public static ReplacementBatchResult<T> Apply<T>(IEnumerable<T> items, Action<T> replace,
        Func<T, bool?> inspectFailure, Func<IReadOnlyList<T>, int> rename)
    {
        var succeeded = new List<T>();
        var failures = new List<ReplacementFailure<T>>();
        var changed = false;
        foreach (var item in items)
        {
            try { replace(item); succeeded.Add(item); changed = true; }
            catch (Exception error)
            {
                bool? partial;
                try { partial = inspectFailure(item); }
                catch (Exception verification) { partial = null; error = new AggregateException(error, verification); }
                changed |= partial == true;
                failures.Add(new(item, error, partial));
            }
        }
        if (succeeded.Count > 0) changed |= rename(succeeded) > 0;
        return new(succeeded.Count, changed, failures);
    }
}
