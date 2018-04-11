using System;

namespace POGOLib.Official.Exceptions
{
    public class SessionUnknowException : Exception
    {
        public SessionUnknowException()
        {
        }

        public SessionUnknowException(string message) : base(message)
        {
        }

        public SessionUnknowException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
