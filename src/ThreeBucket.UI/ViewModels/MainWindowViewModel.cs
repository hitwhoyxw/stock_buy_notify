using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using ThreeBucket.Core;
using ThreeBucket.Core.DataSources;
using ThreeBucket.Core.Models;

namespace ThreeBucket.UI.ViewModels;

public class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly IMarketDataSource _source;
    private string _symbolText = "sh600519,sz000001";
    private string _status = "就绪";
    private ObservableCollection<RealTimeQuote> _quotes = new();

    public MainWindowViewModel()
    {
        // 通过聚合层取数：主源优先，故障自动回退到下一个数据源
        _source = MarketData.DefaultAggregated();
        FetchCommand = new RelayCommand(async _ => await FetchAsync());
    }

    public string SymbolText
    {
        get => _symbolText;
        set => SetField(ref _symbolText, value);
    }

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public ObservableCollection<RealTimeQuote> Quotes
    {
        get => _quotes;
        set => SetField(ref _quotes, value);
    }

    public ICommand FetchCommand { get; }

    private async Task FetchAsync()
    {
        try
        {
            Status = "拉取中...";
            var symbols = SymbolText
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var result = await _source.GetRealTimeQuotesAsync(symbols);
            Quotes = new ObservableCollection<RealTimeQuote>(result);
            Status = $"成功 {result.Count} 条";
        }
        catch (Exception ex)
        {
            Status = $"失败: {ex.Message}";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
