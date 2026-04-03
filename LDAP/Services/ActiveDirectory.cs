using EView360Models.Core;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.DirectoryServices;
using System.DirectoryServices.AccountManagement;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LDAP.Services
{
    public class ActiveDirectory
    {
        public DirectoryEntry de { get; set; }
        public readonly CoreContext _context;
        public PrincipalContext ctx { get; set; }
        public ActiveDirectory()
        {
            de = new DirectoryEntry("LDAP://DC=prod,DC=local", "am185760", "IqbalLahooti@12", AuthenticationTypes.Secure);
            ctx = new PrincipalContext(ContextType.Domain, "prod.local", "DC=prod,DC=local", "am185760", "IqbalLahooti@12");
            _context = new CoreContext(new DbContextOptions<CoreContext>());

            //de = new DirectoryEntry("LDAP://ncr.com", "am185760", "February@145", AuthenticationTypes.Secure);

            //ctx = new PrincipalContext(ContextType.Domain, "ncr.com", "DC=ncr,DC=com", "am185760", "February@145");
        }
    }
}
