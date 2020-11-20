using System;
using AudioPlugSharp;

namespace ExamplePlugin
{
    public class ExamplePlugin : IAudioPluginInfo
    {
        public string Company
        {
            get { return "My Company"; }
        }

        public string Website
        {
            get { return "www.mywebsite.com"; }
        }

        public string Contact
        {
            get { return "contact@my.email"; }
        }

        public string PluginName
        {
            get { return "Example Plugin"; }
        }

        public string PluginCategory
        {
            get { return "Fx"; }
        }

        public string PluginVerstion
        {
            get { return "1.0.0"; }
        }

        public string ProcessorGuid
        {
            get { return "F57703946AFC4EF8BBAFF5DB6DFC9066"; }
        }

        public string ControllerGuid
        {
            get
            {
                return "50E3241D5B754A2B8F85AF829AC758C6";
            }
        }

        public ExamplePlugin()
        {
        }
    }
}
