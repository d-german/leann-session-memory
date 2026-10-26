namespace SessionMemory.Commands
{
    /// <summary>self-test: runs the built-in checks in temporary folders of their own and prints {"ok": true} when all pass.</summary>
    sealed internal class SelfTestCommand : ICommand
    {
        public string Verb => "self-test";

        public bool RequiresArguments => false;

        public int Execute(CommandArguments arguments) => SelfTest.Run();
    }
}
