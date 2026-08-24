using EView360Models.Core;
using LDAP.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.DirectoryServices;
using System.DirectoryServices.AccountManagement;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LDAP.Services;

public class LdapSyncService
{
    private readonly CoreContext _context;
    private readonly LdapSettings _ldapSettings;
    private readonly ILogger<LdapSyncService> _logger;

    public LdapSyncService(CoreContext context, IOptions<LdapSettings> ldapSettings, ILogger<LdapSyncService> logger)
    {
        _context = context;
        _ldapSettings = ldapSettings.Value;
        _logger = logger;
    }

    public async Task SyncAsync(CancellationToken stoppingToken)
    {
        _logger.LogWarning("Starting LDAP sync...");

        DirectoryEntry entry = GetDirectoryEntry();
        using PrincipalContext ctx = GetPrincipalContext();

        if (entry == null || ctx == null)
        {
            _logger.LogError("Failed to bind to Active Directory.");
            return;
        }

        try
        {
            var adGroupDns = await SyncGroupsAsync(entry, stoppingToken);
            await SyncUsersAsync(entry, adGroupDns, stoppingToken);
            await _context.SaveChangesAsync(stoppingToken);
            _logger.LogWarning("LDAP sync completed successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred during LDAP synchronization.");
        }
    }

    private DirectoryEntry GetDirectoryEntry()
    {
        try
        {            
            string connectionString = _ldapSettings.LdapUrl;
            // If the LdapUrl doesn't contain a domain component, gracefully append BaseDN
            if (!connectionString.Contains("DC=", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(_ldapSettings.BaseDN)) 
            {
                connectionString = $"{connectionString.TrimEnd('/')}/{_ldapSettings.BaseDN}";
            }
                
            return new DirectoryEntry(
                connectionString,
                _ldapSettings.ServiceAccountUsername,
                _ldapSettings.ServiceAccountPassword,
                AuthenticationTypes.Secure
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create DirectoryEntry.");
            return null!;
        }
    }

    private PrincipalContext GetPrincipalContext()
    {
        try
        {
            return new PrincipalContext(
                ContextType.Domain,
                _ldapSettings.DomainName,
                _ldapSettings.BaseDN,
                _ldapSettings.ServiceAccountUsername,
                _ldapSettings.ServiceAccountPassword
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create PrincipalContext.");
            return null!;
        }
    }

    private async Task<List<string>> SyncGroupsAsync(DirectoryEntry entry, CancellationToken stoppingToken)
    {
        _logger.LogWarning("Syncing Groups...");

        try
        {
            string groupFilter = "(objectCategory=group)";
            if (_ldapSettings.TargetGroups != null && _ldapSettings.TargetGroups.Length > 0)
            {
                var allowedGroupFilters = string.Join("", _ldapSettings.TargetGroups.Select(g => $"(cn={g})"));
                groupFilter = $"(&{groupFilter}(|{allowedGroupFilters}))";
            }
            else
            {
                groupFilter = $"(&{groupFilter})";
            }

            using var searcher = new DirectorySearcher(entry)
            {
                Filter = groupFilter,
                PageSize = 1000 // Paging for large AD queries
            };
            
            // Performance consideration: Only load needed properties
            searcher.PropertiesToLoad.Add("cn");
            searcher.PropertiesToLoad.Add("distinguishedName");
            using var results = searcher.FindAll();
            var existingGroups = await _context.Groups.ToListAsync(stoppingToken);
            var adGroupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int processedCount = 0;

            var adGroupDns = new List<string>();

            foreach (SearchResult result in results)
            {
                if (stoppingToken.IsCancellationRequested) break;

                try
                {
                    string groupName = GetProperty(result, "cn");
                    string groupDn = GetProperty(result, "distinguishedName");
                    if (string.IsNullOrEmpty(groupName)) continue;

                    if (!string.IsNullOrEmpty(groupDn))
                    {
                        adGroupDns.Add(groupDn);
                    }

                    adGroupNames.Add(groupName);
                    
                    var group = existingGroups.FirstOrDefault(g => g.GroupName.Equals(groupName, StringComparison.OrdinalIgnoreCase));
                    
                    if (group == null)
                    {
                        _context.Groups.Add(new Group
                        {
                            GroupName = groupName,
                            Status = "Active" // Or any default status
                        });
                    }
                    else
                    {
                        group.Status = "Active"; // Sync strategy: if it exists, ensure it is active
                    }

                    processedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing a group record from AD.");
                }
            }

            // Make inactive or handle missing
            foreach (var group in existingGroups)
            {
                if (!adGroupNames.Contains(group.GroupName))
                {
                    group.Status = "Inactive";
                }
            }

            // Immediately save Groups so that new groups get a valid DB-assigned PK (GroupId)
            await _context.SaveChangesAsync(stoppingToken);

            _logger.LogWarning($"Processed {processedCount} groups.");
            return adGroupDns;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while syncing groups");
            return new List<string>();
        }
    }

    private async Task SyncUsersAsync(DirectoryEntry entry, List<string> adGroupDns, CancellationToken stoppingToken)
    {
        _logger.LogWarning("Syncing Users & Group Mappings...");

        try
        {
            string userFilter = "(&(objectCategory=person)(objectClass=user))";
            
            // If we are targeting specific groups, filter users by memberOf those groups
            if (adGroupDns != null && adGroupDns.Count > 0)
            {
                // Note: If you have nested groups, you can use LDAP_MATCHING_RULE_IN_CHAIN: (memberOf:1.2.840.113556.1.4.1941:={dn})
                var memberFilters = string.Join("", adGroupDns.Select(dn => $"(memberOf={dn})"));
                userFilter = $"(&{userFilter}(|{memberFilters}))";
            }
            else if (_ldapSettings.TargetGroups != null && _ldapSettings.TargetGroups.Length > 0)
            {
                 // Target groups were provided but didn't exist in AD, meaning no users to sync
                 userFilter = "(&(objectCategory=person)(objectClass=user)(!(objectClass=*)))";
            }

            using var searcher = new DirectorySearcher(entry)
            {
                Filter = userFilter,
                PageSize = 1000 // Paging for large queries
            };

            searcher.PropertiesToLoad.Add("sAMAccountName");
            searcher.PropertiesToLoad.Add("displayName");
            searcher.PropertiesToLoad.Add("mail");
            searcher.PropertiesToLoad.Add("memberOf");
            using var results = searcher.FindAll();
            
            var existingUsers = await _context.AppUsers
                .Include(u => u.GroupUsers)
                .ToListAsync(stoppingToken);
                
            var existingGroups = await _context.Groups.ToListAsync(stoppingToken);
            
            var adUserLogins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int processedCount = 0;

            foreach (SearchResult result in results)
            {
                if (stoppingToken.IsCancellationRequested) break;

                try
                {
                    string userName = GetProperty(result, "sAMAccountName");
                    string displayName = GetProperty(result, "displayName");
                    string mail = GetProperty(result, "mail");

                    if (string.IsNullOrEmpty(userName)) continue;

                    adUserLogins.Add(userName);

                    var user = existingUsers.FirstOrDefault(u => u.UserLogin.Equals(userName, StringComparison.OrdinalIgnoreCase));

                    if (user == null)
                    {
                        // New User Insert
                        user = new AppUser
                        {
                            UserLogin = userName,
                            UserFullName = string.IsNullOrEmpty(displayName) ? userName : displayName,
                            UserEmail = mail,
                            UserIsActive = true,
                            UserCreationTime = DateTime.UtcNow,
                            IsActiveDirectoryUser = true
                        };
                        _context.AppUsers.Add(user);
                        
                        // Force SaveChanges to generate user.UserId so we can safely map it instantly
                        await _context.SaveChangesAsync(stoppingToken);
                    }
                    else
                    {
                        // Update user
                        user.UserFullName = string.IsNullOrEmpty(displayName) ? userName : displayName;
                        user.UserEmail = mail;
                        user.UserIsActive = true;
                        user.IsActiveDirectoryUser = true;
                        // Note: Intentionally NOT storing passwords locally
                    }

                    // Handle Sub-Group Mappings using proper EF IDs
                    SyncUserGroupMappings(result, user, existingGroups);

                    processedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing a user record from AD.");
                }
            }

            // Inactivate missing users
            foreach (var user in existingUsers.Where(u => u.IsActiveDirectoryUser))
            {
                if (!adUserLogins.Contains(user.UserLogin))
                {
                    user.UserIsActive = false;
                }
            }
            
            // Final comprehensive save
            await _context.SaveChangesAsync(stoppingToken);

            _logger.LogWarning($"Processed {processedCount} users and their group mappings.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while syncing users");
        }
    }

    private void SyncUserGroupMappings(SearchResult result, AppUser user, List<Group> existingGroups)
    {
        try
        {
            var adGroupCNs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // memberOf can contain multiple values
            if (result.Properties.Contains("memberOf"))
            {
                foreach (var memberOdDn in result.Properties["memberOf"])
                {
                    string dn = memberOdDn.ToString() ?? string.Empty;
                    string cn = ExtractCNFromDN(dn);
                    if (!string.IsNullOrEmpty(cn))
                    {
                        adGroupCNs.Add(cn);
                    }
                }
            }

            // We use the navigation properties for the mappings (user.GroupUsers)
            // Ensure user.GroupUsers is not null although initialized in model
            
            var currentMappings = user.GroupUsers.ToList();
            
            // Remove mappings that are no longer in AD
            foreach (var mapping in currentMappings)
            {
                var matchedGroup = existingGroups.FirstOrDefault(g => g.GroupId == mapping.GroupId);
                if (matchedGroup != null && !adGroupCNs.Contains(matchedGroup.GroupName))
                {
                    _context.GroupUsers.Remove(mapping);
                }
            }

            // Add new mappings
            foreach (var adGroupCN in adGroupCNs)
            {
                var group = existingGroups.FirstOrDefault(g => g.GroupName.Equals(adGroupCN, StringComparison.OrdinalIgnoreCase));
                if (group != null)
                {
                    bool userHasGroup = currentMappings.Any(gu => gu.GroupId == group.GroupId);
                    if (!userHasGroup)
                    {
                        _context.GroupUsers.Add(new GroupUser
                        {
                            UserId = user.UserId,
                            GroupId = group.GroupId
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error syncing group mappings for user '{user.UserLogin}'.");
        }
    }

    private string GetProperty(SearchResult result, string propertyName)
    {
        try
        {
            if (result.Properties.Contains(propertyName) && result.Properties[propertyName].Count > 0)
            {
                return result.Properties[propertyName][0]?.ToString() ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
             _logger.LogWarning(ex, $"Error extracting property '{propertyName}'.");
        }
        return string.Empty;
    }

    private string ExtractCNFromDN(string dn)
    {
        try
        {
            // Example DN: CN=AppAdmins,OU=Groups,DC=bank,DC=com -> Returns "AppAdmins"
            if (string.IsNullOrEmpty(dn)) return string.Empty;

            var parts = dn.Split(',');
            var cnPart = parts.FirstOrDefault(p => p.StartsWith("CN=", StringComparison.OrdinalIgnoreCase));
            
            if (cnPart != null && cnPart.Length > 3)
            {
                return cnPart.Substring(3); // Remove "CN="
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, $"Error extracting CN from DN '{dn}'.");
        }

        return string.Empty;
    }
}
