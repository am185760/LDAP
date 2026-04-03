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
    public class Groups : ActiveDirectory
    {
        public async Task HandleGroups()
        {
            await DeleteUnknownGroups();
            await AddNewGroups();
        }

        private async Task AddNewGroups()
        {
            try
            {
                SearchResultCollection results;
                DirectorySearcher ds = new DirectorySearcher(de);
                ds.Filter = "(&(objectCategory=Group))";
                results = ds.FindAll();

                foreach (SearchResult sr in results)
                {
                    string grpName = sr.GetPropertyValue("name");
                    var group = _context.Groups.FirstOrDefault(x => x.GroupName == grpName);
                    if (group == null)
                    {
                        string member = sr.GetPropertyValue("member");
                        Group grp = new();
                        grp.GroupName = grpName;
                        grp.Description = member;
                        _context.Groups.Add(grp);                        
                    }
                }
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {

            }
        }

        private async Task DeleteUnknownGroups()
        {
            try
            {
                List<Group> groups = await _context.Groups.ToListAsync();

                foreach (Group group in groups)
                {
                    // find a user
                    GroupPrincipal _group = GroupPrincipal.FindByIdentity(ctx, group.GroupName);

                    if (_group == null)
                    {
                        _context.GroupUsers.RemoveRange(await _context.GroupUsers.Where(x => x.GroupId == group.GroupId).ToListAsync());
                        _context.GroupRights.RemoveRange(await _context.GroupRights.Where(x => x.GroupId == group.GroupId).ToListAsync());
                        _context.Groups.Remove(group);
                    }
                }
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {

            }
        }
    }
}
