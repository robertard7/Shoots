namespace Shoots.UI.Environment;

public interface IEnvironmentProfileLogger
{
    void LogApplied(EnvironmentProfileResult result);
}

public sealed class NullEnvironmentProfileLogger : IEnvironmentProfileLogger
{
    public void LogApplied(EnvironmentProfileResult result)
    {
        _ = result;
    }
}
