namespace LIGClaw.Desktop.Infrastructure.Shell;

internal static class AtomicSettingsMutation
{
    public static void Execute(Action apply, params Action[] rollback)
    {
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(rollback);
        try
        {
            apply();
        }
        catch (Exception applyException)
        {
            var rollbackFailures = new List<Exception>();
            foreach (var restore in rollback)
            {
                try { restore(); }
                catch (Exception exception) { rollbackFailures.Add(exception); }
            }
            if (rollbackFailures.Count > 0)
                throw new AggregateException(
                    "설정 저장과 이전 상태 복원에 실패했습니다.",
                    [applyException, .. rollbackFailures]);
            throw;
        }
    }
}
