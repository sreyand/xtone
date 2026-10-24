using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace XTONE.Views;

public sealed class SliderCommittedEventArgs(double value) : EventArgs
{
    public double Value { get; } = value;
}

/// <summary>
/// Distinguishes an assistive technology's explicit RangeValue command from a
/// bound playback-clock update. Neither ordinary bindings nor timers commit it.
/// </summary>
public sealed class ListeningSlider : Slider
{
    public event EventHandler<SliderCommittedEventArgs>? AutomationValueCommitted;
    protected override AutomationPeer OnCreateAutomationPeer() => new ListeningSliderPeer(this);

    private sealed class ListeningSliderPeer(ListeningSlider slider) : SliderAutomationPeer(slider), IRangeValueProvider
    {
        public override object GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.RangeValue ? this : base.GetPattern(patternInterface);
        bool IRangeValueProvider.IsReadOnly => !slider.IsEnabled;
        double IRangeValueProvider.Value => slider.Value;
        double IRangeValueProvider.Maximum => slider.Maximum;
        double IRangeValueProvider.Minimum => slider.Minimum;
        double IRangeValueProvider.LargeChange => slider.LargeChange;
        double IRangeValueProvider.SmallChange => slider.SmallChange;
        void IRangeValueProvider.SetValue(double value)
        {
            if (!slider.IsEnabled) throw new ElementNotEnabledException();
            if (!double.IsFinite(value) || value < slider.Minimum || value > slider.Maximum)
                throw new ArgumentOutOfRangeException(nameof(value));
            slider.SetCurrentValue(ValueProperty, value);
            slider.AutomationValueCommitted?.Invoke(slider, new SliderCommittedEventArgs(value));
        }
    }
}
