namespace ProjectPerseus
{
    public class Log
    {
        private static void Message(string message)
        {
            System.Console.WriteLine(message);
        }
        
        public static void Error(string message)
        {
            Message($"[ERROR] {message}");
        }
        
        public static void Warn(string message)
        {
            Message($"[WARNING] {message}");
        }
    }
}