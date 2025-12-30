using System.Windows;

namespace NVSPlotter
{
    public partial class ReferenceImageView : System.Windows.Controls.UserControl
    {
        public ReferenceImageView()
        {
            InitializeComponent();
        }

        // Expose events for parent to subscribe
        public event RoutedEventHandler? ImportClicked
        {
            add => ImportImageBtn.Click += value;
            remove => ImportImageBtn.Click -= value;
        }

        public event RoutedEventHandler? ClearClicked
        {
            add => ClearImageBtn.Click += value;
            remove => ClearImageBtn.Click -= value;
        }

        public event RoutedPropertyChangedEventHandler<double>? RotationChanged
        {
            add => ImageRotateSlider.ValueChanged += value;
            remove => ImageRotateSlider.ValueChanged -= value;
        }

        public event RoutedEventHandler? RotationResetClicked
        {
            add => ImageRotateResetBtn.Click += value;
            remove => ImageRotateResetBtn.Click -= value;
        }

        public event System.Windows.Controls.SelectionChangedEventHandler? FilterChanged
        {
            add => ImageFilterCombo.SelectionChanged += value;
            remove => ImageFilterCombo.SelectionChanged -= value;
        }

        public event RoutedPropertyChangedEventHandler<double>? FilterSliderChanged
        {
            add => FilterControlSlider.ValueChanged += value;
            remove => FilterControlSlider.ValueChanged -= value;
        }

        public System.Windows.Controls.CheckBox ImageLockCheckBox => ImageLockCheck;
        public System.Windows.Controls.Slider ImageRotateSliderControl => ImageRotateSlider;
        public System.Windows.Controls.TextBlock ImageRotateValueText => ImageRotateValue;
        public System.Windows.Controls.ComboBox ImageFilterComboBox => ImageFilterCombo;
        public System.Windows.Controls.Slider FilterControlSliderControl => FilterControlSlider;
    }
}
