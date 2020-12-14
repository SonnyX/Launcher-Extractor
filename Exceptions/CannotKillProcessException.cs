using System;

namespace SelfUpdateExecutor.Exceptions
{
    internal class CannotKillProcessException : Exception
    {
        public CannotKillProcessException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}