using AudioPlugSharp;
using AudioPlugSharpJack;
using SimpleExample;

namespace JackHostTest
{
    internal class Program
    {
        static void Main(string[] args)
        {
            JackHost<SimpleExamplePlugin> host = new JackHost<SimpleExamplePlugin>(new SimpleExamplePlugin());

            host.Run();

            Logger.FlushAndShutdown();
        }
    }
}
