namespace ProjectPerseus
{
    public class Log
    {
        private static void LogMessage(string message)
        {
            System.Console.WriteLine(message);
        }
        private static void LogMessage(MessageType type, string message)
        {
            LogMessage($"[{type.ToString()}] {message}");
        }
        
        
        public static void Error(string message)
        {
            LogMessage(MessageType.Error, message);
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