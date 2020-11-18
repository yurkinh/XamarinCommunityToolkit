using System.Windows.Input;
using Xamarin.CommunityToolkit.Effects;
using Xamarin.Forms;
using Xamarin.Forms.Xaml;

namespace Xamarin.CommunityToolkit.Sample.Pages.Effects
{
	[XamlCompilation(XamlCompilationOptions.Compile)]
	public partial class TouchEffectPage
	{
		public TouchEffectPage()
			=> InitializeComponent();

		public ICommand Command { get; } = new Command(() => Application.Current.MainPage.DisplayAlert("Command", "The command was executed", "OK"));

		void OnCompleted(object sender, TouchCompletedEventArgs args)
			=> Application.Current.MainPage.DisplayAlert("Completed", "The Completed event was raised", "OK");
	}
}
