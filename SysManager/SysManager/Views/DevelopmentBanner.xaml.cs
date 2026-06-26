// SysManager · DevelopmentBanner
// Author: laurentiu021 · https://github.com/laurentiu021/SystemManager
// License: MIT

using System.Windows.Controls;

namespace SysManager.Views;

/// <summary>
/// Banner shown at the top of a feature view that is implemented but not yet
/// QA-verified. Pairs with the sidebar "PREVIEW" pill driven by
/// <see cref="ViewModels.NavItem.IsInDevelopment"/>.
/// </summary>
public partial class DevelopmentBanner : UserControl
{
    public DevelopmentBanner() => InitializeComponent();
}
