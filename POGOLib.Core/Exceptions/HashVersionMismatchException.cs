using System;

namespace POGOLib.Official.Exceptions
{
    public class HashVersionMismatchException : Exception
    {
        public HashVersionMismatchException(string message) : base(message)
        {
        }
    }
}
