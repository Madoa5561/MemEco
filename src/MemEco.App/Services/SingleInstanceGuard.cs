using System.Threading;

namespace MemEco.App.Services;

/// <summary>
/// 二重起動の検出（名前付き Mutex）と、2 個目からの「パネルを表示」通知（名前付きイベント）。
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\MemEco.SingleInstance.v1";
    private const string ShowEventName = @"Local\MemEco.ShowPanel.v1";

    private readonly Mutex _mutex;
    private EventWaitHandle? _showEvent;
    private Thread? _listener;
    private volatile bool _stop;
    private bool _disposed;

    public SingleInstanceGuard()
    {
        _mutex = new Mutex(initiallyOwned: false, MutexName, out bool createdNew);
        IsFirstInstance = createdNew;

        if (createdNew)
        {
            try
            {
                _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            }
            catch
            {
                _showEvent = null;
            }
        }
    }

    /// <summary>このプロセスが最初のインスタンスか。</summary>
    public bool IsFirstInstance { get; }

    /// <summary>既存インスタンスへパネル表示を通知する（2 個目のプロセスから呼ぶ）。</summary>
    public void SignalShow()
    {
        if (_showEvent != null)
        {
            try
            {
                _showEvent.Set();
            }
            catch
            {
                // ignore
            }
            return;
        }

        try
        {
            using EventWaitHandle handle = EventWaitHandle.OpenExisting(ShowEventName);
            handle.Set();
        }
        catch
        {
            // 既存インスタンスが見つからない場合は何もしない
        }
    }

    /// <summary>待機スレッドを開始する。通知を受けたら onShow を（別スレッドで）呼ぶ。</summary>
    public void Listen(Action onShow)
    {
        if (_showEvent == null || _listener != null)
        {
            return;
        }

        _listener = new Thread(() =>
        {
            while (!_stop)
            {
                try
                {
                    if (!_showEvent.WaitOne(TimeSpan.FromMilliseconds(400)))
                    {
                        continue;
                    }

                    if (_stop)
                    {
                        return;
                    }

                    onShow();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch
                {
                    Thread.Sleep(200);
                }
            }
        })
        {
            IsBackground = true,
            Name = "MemEco.ShowListener",
        };
        _listener.Start();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _stop = true;

        try
        {
            _showEvent?.Set();
        }
        catch
        {
            // ignore
        }

        try
        {
            _listener?.Join(500);
        }
        catch
        {
            // ignore
        }

        _showEvent?.Dispose();
        _mutex.Dispose();
    }
}
