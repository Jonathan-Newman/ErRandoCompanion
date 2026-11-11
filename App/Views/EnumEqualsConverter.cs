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

/// <summary>Turns true into false and back, for enabling a control while something is not happening.</summary>
public sealed class NotConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
