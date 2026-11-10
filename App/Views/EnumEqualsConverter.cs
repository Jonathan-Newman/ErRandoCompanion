using System.Globalization;
using System.Windows.Data;

namespace ErRandoCompanion.App.Views;

/// <summary>Binds a radio button to one value of an enum, named by the ConverterParameter.</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value.ToString() == (string)parameter;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Enum.Parse(targetType, (string)parameter) : Binding.DoNothing;
}
