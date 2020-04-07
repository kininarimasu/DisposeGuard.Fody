using System;
using System.Threading;
using System.Threading.Tasks;

public static class XXX
{
    public static int I = 0;

    public static void OnDisposableFinalize(IDisposable o, bool disposed)
    {
        Console.WriteLine($">>> {o} | {disposed}");
        o.Dispose();
        I++;
    }
}

namespace DisposeGuard.Fody.Tests
{
    class Program
    {
        // static TestClass1 t1 = null;
        // static TestClass2 t2 = null;

        static void Main(string[] args)
        {
            // Object finalizer test
            {
                XXX.I = 0;
                new TestClass1();
                gc();
                Assert(XXX.I == 1);
                Log("=== Test #1 passed ===");
            }

            // Object disposed test
            {
                var t = new TestClass1();
                t.SomeMethod();
                t.Dispose();
                var b = false;
                try
                {
                    t.SomeMethod();
                }
                catch (ObjectDisposedException)
                {
                    b = true;
                }
                Assert(b);
                t = null;
                gc();
                Log("=== Test #2 passed ===");
            }

            Log("end.");
        }

        static void gc()
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);
            GC.WaitForPendingFinalizers();
            GC.WaitForFullGCComplete();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);
        }

        static void Assert(bool b)
        {
            if (!b)
                throw new Exception("Assert fail");
        }

        static void Log(object s)
        {
            Console.WriteLine(s);
        }
    }

    class TestClass1 : IDisposable
    {
        public TestClass1() => Console.WriteLine($"{nameof(TestClass1)}..ctor");
        public void Dispose() => Console.WriteLine($"{nameof(TestClass1)}.Dispose");
        ~TestClass1() => Console.WriteLine($"{nameof(TestClass1)}.Finalize");
        public void SomeMethod() => Console.WriteLine($"{nameof(TestClass1)}.SomeMethod");
    }

    // class TestClass2 : IDisposable
    // {
    //     public TestClass2() => Console.WriteLine($"{nameof(TestClass2)}..ctor");
    //     public void Dispose() => Console.WriteLine($"{nameof(TestClass2)}.Dispose");
    //     ~TestClass2() => Console.WriteLine($"{nameof(TestClass2)}.Finalize");
    //     public void SomeMethod() => Console.WriteLine($"{nameof(TestClass2)}.SomeMethod");
    // }
}
