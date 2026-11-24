using System;
using OriCoop.Rules;

static class PopupDebtTests
{
    sealed class Box
    {
        public int Hash;
        public override int GetHashCode() { return Hash; }
    }
    sealed class Debt { }

    internal static void RunAll(Action<string, Action> run, Action<bool, string> check)
    {
        run("popup prune tolerates reentrant prune", delegate {
            ScreenDebts<Box, Debt> popups = new ScreenDebts<Box, Debt>();
            popups.Set(new Box(), new Debt());
            popups.Set(new Box(), new Debt());
            int retired = 0;
            popups.Prune(box => true, delegate {
                if (++retired == 1) popups.Prune(box => true, debt => retired++);
            });
            check(retired == 2 && popups.Count == 0, "both debts must be retired exactly once");
        });
        run("popup prune leaves replacements registered", delegate {
            ScreenDebts<Box, Debt> popups = new ScreenDebts<Box, Debt>();
            Box first = new Box(), second = new Box();
            Debt a = new Debt(), b = new Debt(), replacement = new Debt();
            popups.Set(first, a);
            popups.Set(second, b);
            Box replaced = null;
            popups.Prune(box => true, delegate(Debt debt) {
                check(replaced == null, "prune acted on a superseded snapshot entry");
                replaced = debt == a ? second : first;
                popups.Set(replaced, replacement);
            });
            Debt current;
            check(popups.Count == 1 && popups.TryGet(replaced, out current) && current == replacement, "replacement was pruned");
        });
        run("popup prune can remove a key after its object hash changes", delegate {
            ScreenDebts<Box, Debt> popups = new ScreenDebts<Box, Debt>();
            Box box = new Box();
            popups.Set(box, new Debt());
            box.Hash = 1;
            int retired = 0;
            popups.Prune(key => true, debt => retired++);
            check(retired == 1 && popups.Count == 0, "unreachable debt survived prune");
        });
        run("popup prune detaches a debt even if its cleanup throws", delegate {
            ScreenDebts<Box, Debt> popups = new ScreenDebts<Box, Debt>();
            popups.Set(new Box(), new Debt());
            Exception failure = new InvalidOperationException("cleanup failed");
            try { popups.Prune(box => true, debt => { throw failure; }); }
            catch (InvalidOperationException e) { check(e == failure, "unexpected prune error"); }
            check(popups.Prune(box => true, debt => { throw new Exception("cleanup retried"); }) == 0,
                "bad debt survived cleanup");
        });
        run("popup snapshot skips entries changed by callbacks", delegate {
            ScreenDebts<Box, Debt> popups = new ScreenDebts<Box, Debt>();
            Box first = new Box(), second = new Box();
            Debt a = new Debt(), b = new Debt();
            popups.Set(first, a);
            popups.Set(second, b);
            int visited = 0;
            foreach (var pair in popups.CurrentSnapshot())
            {
                check(++visited == 1, "snapshot yielded a removed entry");
                if (pair.Key == first) popups.Remove(second, b);
                else popups.Remove(first, a);
            }
            check(visited == 1, "snapshot skipped the current entry");
        });
        run("popup lookup accepts missing and null keys", delegate {
            ScreenDebts<Box, Debt> popups = new ScreenDebts<Box, Debt>();
            Debt current;
            check(!popups.TryGet(new Box(), out current) && !popups.TryGet(null, out current), "absent debt was returned");
        });
    }
}
