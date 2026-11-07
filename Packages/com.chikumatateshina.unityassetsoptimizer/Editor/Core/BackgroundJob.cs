using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using UnityEditor;

namespace ChikumaTateshina.UnityAssetsOptimizer
{
    /// <summary>
    /// 処理の進捗。コルーチンから yield すると表示用の段階名と進捗が更新される。
    /// </summary>
    public sealed class JobProgress
    {
        public readonly string Stage;
        public readonly float Value;

        public JobProgress(string stage, float value)
        {
            Stage = stage;
            Value = value;
        }
    }

    /// <summary>
    /// Unity API はメインスレッドでしか使えないため、処理をコルーチン（IEnumerator）として分割し、
    /// EditorApplication.update で1フレームあたりの時間上限内だけ進める。エディタの操作を妨げない。
    /// <para>
    /// yield できる値: null（次の区切りへ）/ JobProgress（進捗更新）/ IEnumerator（入れ子の処理）/
    /// Task（別スレッドの処理。完了まで待つ。Unity API を使わない処理のみ）。
    /// </para>
    /// </summary>
    public sealed class BackgroundJob
    {
        /// <summary>1回の update で処理に使う時間の上限（ミリ秒）。</summary>
        public const double FrameBudgetMs = 12;

        readonly Stack<IEnumerator> _stack = new Stack<IEnumerator>();
        Task _waiting;
        bool _running;

        public string Name { get; }
        public string Stage { get; private set; } = "待機中";
        public float Progress { get; private set; }
        public bool IsRunning => _running;
        public bool IsCompleted { get; private set; }
        public bool IsCancelled { get; private set; }
        public Exception Error { get; private set; }

        public event Action<BackgroundJob> Finished;

        public BackgroundJob(string name, IEnumerator routine)
        {
            Name = name;
            _stack.Push(routine);
        }

        public void Start()
        {
            if (_running || IsCompleted) return;
            _running = true;
            EditorApplication.update += Tick;
        }

        /// <summary>中断する。実行中のコルーチンは Dispose され、finally（一時オブジェクトの破棄）が実行される。</summary>
        public void Cancel()
        {
            if (IsCompleted) return;
            IsCancelled = true;
            Finish();
        }

        void Tick()
        {
            var watch = Stopwatch.StartNew();
            try
            {
                while (watch.Elapsed.TotalMilliseconds < FrameBudgetMs)
                {
                    if (_waiting != null)
                    {
                        if (!_waiting.IsCompleted) return;
                        var task = _waiting;
                        _waiting = null;
                        if (task.IsFaulted) throw task.Exception?.GetBaseException() ?? new Exception("background task failed");
                    }
                    if (_stack.Count == 0)
                    {
                        Finish();
                        return;
                    }
                    var top = _stack.Peek();
                    if (!top.MoveNext())
                    {
                        (_stack.Pop() as IDisposable)?.Dispose();
                        continue;
                    }
                    switch (top.Current)
                    {
                        case JobProgress p:
                            Stage = p.Stage;
                            Progress = p.Value;
                            break;
                        case IEnumerator nested:
                            _stack.Push(nested);
                            break;
                        case Task t:
                            _waiting = t;
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                Error = e;
                Finish();
            }
        }

        void Finish()
        {
            if (IsCompleted) return;
            EditorApplication.update -= Tick;
            _running = false;
            IsCompleted = true;
            while (_stack.Count > 0) (_stack.Pop() as IDisposable)?.Dispose();
            Finished?.Invoke(this);
        }

        /// <summary>コルーチンを同期的に最後まで実行する（本実行・テスト用）。Task は完了を待つ。</summary>
        public static void RunToCompletion(IEnumerator routine)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(routine);
            try
            {
                while (stack.Count > 0)
                {
                    var top = stack.Peek();
                    if (!top.MoveNext())
                    {
                        (stack.Pop() as IDisposable)?.Dispose();
                        continue;
                    }
                    if (top.Current is IEnumerator nested) stack.Push(nested);
                    else if (top.Current is Task t) t.GetAwaiter().GetResult();
                }
            }
            finally
            {
                while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose();
            }
        }
    }
}
