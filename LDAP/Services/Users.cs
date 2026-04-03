using EView360Models.Core;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.DirectoryServices;
using System.DirectoryServices.AccountManagement;

namespace LDAP.Services
{
    public class Users: ActiveDirectory
    {
        public async Task HandleUsers()
        {
            await DeleteUnknownUsers();
            await AddNewUsers();
        }


        public List<string> GetAllUsers()
        {
            List<string> users = new List<string>();

            try
            {
                DirectorySearcher ds = new DirectorySearcher(de);
                ds.Filter = "(&(objectCategory=User)(objectClass=person))";

                SearchResultCollection results = ds.FindAll();

                foreach (SearchResult sr in results)
                {
                    string userName = sr.GetPropertyValue("name");
                    if (!string.IsNullOrEmpty(userName))
                    {
                        users.Add(userName);
                    }
                }
            }
            catch (Exception ex)
            {
                // log exception
            }

            return users;
        }

        private async Task AddNewUsers()
        {
            try
            {
                SearchResultCollection results;
                DirectorySearcher ds = new DirectorySearcher(de);
                ds.Filter = "(&(objectCategory=User)(objectClass=person))";


                results = ds.FindAll();

                foreach (SearchResult sr in results)
                {
                    string userName = sr.GetPropertyValue("name");

                    var user = _context.AppUsers.FirstOrDefault(x => x.UserFullName == userName);
                    if (user == null)
                    {
                        string memberOf = sr.GetPropertyValue("memberOf");
                        string email = sr.GetPropertyValue("distinguishedName");
                        AppUser appUser = new();
                        appUser.UserFullName = userName;
                        appUser.UserEmail = email;
                        _context.AppUsers.Add(appUser);
                    }
                }
                await _context.SaveChangesAsync();
            }
            catch (Exception ex)
            {

            }
        }

        private async Task DeleteUnknownUsers()
        {
            try
            {
                List<AppUser> appUsers = await _context.AppUsers.ToListAsync();

                foreach (AppUser appUser in appUsers)
                {
                    // find a user
                    UserPrincipal user = UserPrincipal.FindByIdentity(ctx, appUser.UserLogin);

                    if (user == null)
                    {
                        _context.GroupUsers.Remove(await _context.GroupUsers.FirstOrDefaultAsync(x => x.UserId == appUser.UserId));
                        _context.UserAtms.RemoveRange(await _context.UserAtms.Where(x => x.UserId == appUser.UserId).ToListAsync());

                        _context.AppUsers.Remove(appUser);
                    }
                }
                await _context.SaveChangesAsync();
            }
            catch(Exception ex) 
            {

            }
        }
    }
}
