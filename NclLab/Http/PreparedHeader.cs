using System;
using System.Collections.Generic;
using System.Text;

namespace NclLab.Http
{
    /// <summary>
    /// A header that has been pre-encoded and cached for the connection, perhaps inserted into HTTP/2 dynamic table, etc.
    /// </summary>
    public sealed class PreparedHeader : IDisposable
    {
        public void Dispose()
        {
            throw new NotImplementedException();
        }
    }
}
