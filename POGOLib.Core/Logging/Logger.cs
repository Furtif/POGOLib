using System.Collections.Generic;

namespace POGOLib.Official.Logging
{
    public delegate void LogOutputDelegate(LogLevel logLevel, string message);

    public class Logger
    {
        private readonly List<LogOutputDelegate> LogOutputs = new List<LogOutputDelegate>();

        public void RegisterLogOutput(LogOutputDelegate logOutput)
        {
            LogOutputs.Add(logOutput);
        }

        public void Debug(string message)
        {
            Output(LogLevel.Debug, message);
        }

        public void Info(string message)
        {
            Output(LogLevel.Info, message);
        }

        public void Notice(string message)
        {
            Output(LogLevel.Notice, message);
        }

        public void Warn(string message)
        {
            Output(LogLevel.Warn, message);
        }

        public void Error(string message)
        {
            Output(LogLevel.Error, message);
        }

        private void Output(LogLevel logLevel, string message)
        {
            foreach(var logOutput in LogOutputs)
            {
                logOutput(logLevel, message);
            }
        }
    }
}
