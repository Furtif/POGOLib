using System;

namespace POGOLib.Official.Exceptions
{
    public class WrongCredentialsException : Exception
    {

        public WrongCredentialsException(string message) : base(message)
        {
            
        }
    }
}
