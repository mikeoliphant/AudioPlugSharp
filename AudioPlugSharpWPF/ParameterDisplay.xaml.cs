using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace AudioPlugSharpWPF
{
    /// <summary>
    /// Interaction logic for ParameterDisplay.xaml
    /// </summary>
    public partial class ParameterDisplay : UserControl
    {
        public ParameterDisplay()
        {
            InitializeComponent();
        }

        // A dial drag is one edit gesture on its parameter
        void Dial_EditStarted(object sender, EventArgs e)
        {
            ((sender as FrameworkElement)?.DataContext as AudioPlugSharp.AudioPluginParameter)?.BeginEdit();
        }

        void Dial_EditEnded(object sender, EventArgs e)
        {
            ((sender as FrameworkElement)?.DataContext as AudioPlugSharp.AudioPluginParameter)?.EndEdit();
        }
    }
}
