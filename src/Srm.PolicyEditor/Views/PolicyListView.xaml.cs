using System.Windows.Controls;
using Srm.PolicyEditor.ViewModels;

namespace Srm.PolicyEditor.Views;

public partial class PolicyListView : UserControl
{
    public PolicyListView(PolicyListViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
