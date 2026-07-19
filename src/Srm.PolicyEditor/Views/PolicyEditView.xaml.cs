using System.Windows.Controls;
using Srm.PolicyEditor.ViewModels;

namespace Srm.PolicyEditor.Views;

public partial class PolicyEditView : UserControl
{
    public PolicyEditView(PolicyEditViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
