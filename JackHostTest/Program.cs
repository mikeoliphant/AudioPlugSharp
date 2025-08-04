using AudioPlugSharp;
using AudioPlugSharpJack;
using SimpleExample;

namespace JackHostTest
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var dec = new DecibelParameter
            {
                ID = "gain",
                Name = "Gain",
                //MinValue = -20,
                //MaxValue = 20,
                DefaultValue = 0,
                ValueFormat = "{0:0.0}dB"
            };

            double denorm = dec.GetValueDenormalized(0.5);


            JackHost<SimpleExamplePlugin> host = new JackHost<SimpleExamplePlugin>(new SimpleExamplePlugin());

            host.Run();

            Logger.FlushAndShutdown();
        }
    }
}
