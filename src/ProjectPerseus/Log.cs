using Sentry;

namespace ProjectPerseus
{
    public static class Log
    {
        private static void LogConsoleMessage(string message)
        {
            System.Console.WriteLine(message);
        }

        private static void LogMessage(MessageType type, string message)
        {
            SentrySdk.CaptureMessage(message, ToSentryLevel(type));
            LogConsoleMessage($"[{type.ToString()}] {message}");
        }
        
        private static void LogException(System.Exception e)
        {
            SentrySdk.CaptureException(e);
            LogConsoleMessage($"[Exception] {e.Message}");
        }

        private static SentryLevel ToSentryLevel(MessageType type)
        {
            switch (type)
            {
                case MessageType.Error:
                    return SentryLevel.Error;
                case MessageType.Warning:
                    return SentryLevel.Warning;
                case MessageType.Info:
                    return SentryLevel.Info;
                default:
                    throw new System.Exception($"Unknown MessageType: {type}");
            }
        }

        public static void Error(string message)
        {
            LogMessage(MessageType.Error, message);
        }

        public static void Exception(System.Exception e)
        {
            LogException(e);
        }

        public static void Warn(string message)
        {
            LogMessage(MessageType.Warning, message);
        }

        public static void Info(string message)
        {
            LogMessage(MessageType.Info, message);
        }

        // message type enum
        private enum MessageType
        {
            Error,
            Warning,
            Info
        }
    }
}