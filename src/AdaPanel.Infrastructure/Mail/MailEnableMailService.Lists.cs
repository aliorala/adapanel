using System.Text.Json;
using AdaPanel.Core.Common;
using AdaPanel.Core.Dtos.Mail;

namespace AdaPanel.Infrastructure.Mail;

public sealed partial class MailEnableMailService
{
    // Native PostingMode=0 checks an address, not an authenticated member.
    internal static void ValidateMailEnableListPolicy(string? policy, IReadOnlyCollection<string>? allowedSenders)
    {
        if ((policy is not null && policy != MailListPostingPolicy.Anyone) || allowedSenders is { Count: > 0 })
            throw new AppException("mail.mailenable_list_policy_unsupported", 409, 409);
    }

    public async Task<MailWorkspaceDto> CreateMailListAsync(CreateMailListRequest request, CancellationToken cancellationToken = default)
    {
        ValidateMailEnableListPolicy(request.PostingPolicy, request.AllowedSenders);
        var domain = ValidateDomain(request.Domain);
        var local = ValidateLocalAddress(request.Address, domain, false);
        var description = request.Description ?? string.Empty;
        if (description.Length > 256 || description.Any(char.IsControl))
            throw new AppException("mail.mailenable_list_description_invalid", 400, 400);
        var recipients = NormalizeListRecipients(request.Recipients);
        if (recipients.Contains($"{local}@{domain}", StringComparer.OrdinalIgnoreCase))
            throw new AppException("mail.mailenable_list_self_reference", 400, 400);
        using var mutation = await AcquireMutationAsync(cancellationToken);
        await RunVerifiedCommandAsync(CreateNativeListScript(domain, local, description, recipients), cancellationToken);
        var workspace = await VerifiedListWorkspaceAsync(domain, $"{local}@{domain}", true, cancellationToken);
        var created = workspace.Lists.Single(x => x.Address.Equals($"{local}@{domain}", StringComparison.OrdinalIgnoreCase));
        if (created.Description != description || !new HashSet<string>(created.Recipients, StringComparer.OrdinalIgnoreCase).SetEquals(recipients))
            throw new AppException("mail.mailenable_lists_unmeasured", 502, 502);
        return workspace;
    }

    public async Task<MailWorkspaceDto> SetMailListRecipientsAsync(SetMailListRecipientsRequest request, CancellationToken cancellationToken = default)
    {
        ValidateMailEnableListPolicy(request.PostingPolicy, request.AllowedSenders);
        var (local, domain) = SplitAddress(request.Address);
        var replace = request.Recipients is null ? null : NormalizeListRecipients(request.Recipients);
        var add = NormalizeListRecipients(request.Add);
        var remove = NormalizeListRecipients(request.Remove);
        using var mutation = await AcquireMutationAsync(cancellationToken);
        await RunVerifiedCommandAsync(UpdateNativeListScript(domain, $"{local}@{domain}", replace, add, remove,
            request.PostingPolicy is not null), cancellationToken);
        var workspace = await VerifiedListWorkspaceAsync(domain, $"{local}@{domain}", true, cancellationToken);
        var actual = new HashSet<string>(workspace.Lists.Single(x => x.Address.Equals($"{local}@{domain}", StringComparison.OrdinalIgnoreCase)).Recipients, StringComparer.OrdinalIgnoreCase);
        if (remove.Any(actual.Contains) || add.Except(remove, StringComparer.OrdinalIgnoreCase).Any(x => !actual.Contains(x)) ||
            (replace is not null && !actual.SetEquals(replace.Concat(add).Except(remove, StringComparer.OrdinalIgnoreCase))))
            throw new AppException("mail.mailenable_lists_unmeasured", 502, 502);
        return workspace;
    }

    public async Task<MailWorkspaceDto> DeleteMailListAsync(string address, CancellationToken cancellationToken = default)
    {
        var (local, domain) = SplitAddress(address);
        using var mutation = await AcquireMutationAsync(cancellationToken);
        await RunVerifiedCommandAsync(DeleteNativeListScript(domain, $"{local}@{domain}"), cancellationToken);
        return await VerifiedListWorkspaceAsync(domain, $"{local}@{domain}", false, cancellationToken);
    }

    public Task<MailWorkspaceDto> AddMailListAliasAsync(MailListAliasRequest request, CancellationToken cancellationToken = default)
        => ChangeNativeListAliasAsync(request, true, cancellationToken);

    public Task<MailWorkspaceDto> RemoveMailListAliasAsync(MailListAliasRequest request, CancellationToken cancellationToken = default)
        => ChangeNativeListAliasAsync(request, false, cancellationToken);

    private async Task<MailWorkspaceDto> ChangeNativeListAliasAsync(MailListAliasRequest request, bool add, CancellationToken cancellationToken)
    {
        var (local, domain) = SplitAddress(request.ListAddress);
        var aliasInput = request.Alias.Contains('@') ? request.Alias : $"{request.Alias}@{domain}";
        var (aliasLocal, aliasDomain) = SplitAddress(aliasInput);
        var alias = $"{aliasLocal}@{aliasDomain}";
        var address = $"{local}@{domain}";
        if (alias.Equals(address, StringComparison.OrdinalIgnoreCase))
            throw new AppException("mail.list_alias_is_list_address", 409, 409);
        using var mutation = await AcquireMutationAsync(cancellationToken);
        await RunVerifiedCommandAsync(ListAliasScript(domain, address, aliasDomain, alias, add), cancellationToken);
        var workspace = await VerifiedListWorkspaceAsync(domain, address, true, cancellationToken);
        var actual = workspace.Lists.Single(x => x.Address.Equals(address, StringComparison.OrdinalIgnoreCase));
        if (actual.Aliases.Contains(alias, StringComparer.OrdinalIgnoreCase) != add)
            throw new AppException("mail.mailenable_lists_unmeasured", 502, 502);
        return workspace;
    }

    private async Task<MailWorkspaceDto> VerifiedListWorkspaceAsync(string domain, string address, bool mustExist, CancellationToken cancellationToken)
    {
        var workspace = await GetWorkspaceAsync(domain, cancellationToken);
        if (!workspace.Configured || !workspace.ListsMeasured ||
            workspace.Lists.Any(x => x.Address.Equals(address, StringComparison.OrdinalIgnoreCase)) != mustExist)
            throw new AppException("mail.mailenable_lists_unmeasured", 502, 502);
        return workspace;
    }

    internal async Task<IReadOnlyCollection<MailListDto>> ReadNativeListsAsync(string domain, CancellationToken cancellationToken)
    {
        var output = await RunComAsync(ReadNativeListsScript(ValidateDomain(domain)), cancellationToken);
        var json = ParseJson(output);
        if (json is null || json.Value.ValueKind != JsonValueKind.Object ||
            !json.Value.TryGetProperty("lists", out var lists) || lists.ValueKind != JsonValueKind.Array)
            throw new AppException("mail.mailenable_lists_unmeasured", 502, 502);
        var result = new List<MailListDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in lists.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("address", out var address) || address.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("description", out var description) || description.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("postingPolicy", out var policy) || policy.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("recipients", out var recipients) || recipients.ValueKind != JsonValueKind.Array ||
                !item.TryGetProperty("aliases", out var aliases) || aliases.ValueKind != JsonValueKind.Array ||
                recipients.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String) ||
                aliases.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String) || !seen.Add(address.GetString()!))
                throw new AppException("mail.mailenable_lists_unmeasured", 502, 502);
            var (local, actualDomain) = SplitAddress(address.GetString()!);
            if (!actualDomain.Equals(domain, StringComparison.OrdinalIgnoreCase))
                throw new AppException("mail.mailenable_lists_unmeasured", 502, 502);
            result.Add(new MailListDto
            {
                Address = $"{local}@{actualDomain}", Description = description.GetString()!, PostingPolicy = policy.GetString()!,
                Recipients = NormalizeListRecipients(recipients.EnumerateArray().Select(x => x.GetString()!).ToArray()),
                Aliases = NormalizeListRecipients(aliases.EnumerateArray().Select(x => x.GetString()!).ToArray())
            });
        }
        return result;
    }

    private static string[] NormalizeListRecipients(IReadOnlyCollection<string> addresses)
    {
        if (addresses.Count > 10000) throw new AppException("mail.invalid_address", 400, 400);
        return addresses.Select(address =>
        {
            var (local, domain) = SplitAddress(address);
            var normalized = $"{local}@{domain}";
            if (normalized.Length > 249) throw new AppException("mail.invalid_address", 400, 400);
            return normalized;
        }).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string ListPsArray(IEnumerable<string> values)
        => "@(" + string.Join(",", values.Select(value => "'" + PsLiteralRaw(value) + "'")) + ")";

    internal const string NativeListHeader = """
        $ErrorActionPreference = 'Stop'
        function Assert-ListDomain([string]$domain, [string]$po) {
          $d = New-Object -ComObject 'MEAOSM.Domain'; $d.DomainName = $domain
          if ($d.GetDomain() -ne 1 -or $d.AccountName -ine $po) { throw 'List domain ownership could not be verified.' }
        }
        function Find-List([string]$po, [string]$name) {
          $l = New-Object -ComObject 'MEAOLS.List'
          $l.AccountName=$po; $l.ListName=$name; $l.ListStatus=-1; $l.ListType=-1
          if ($l.GetList() -ne 1) { return $null }
          if ($l.AccountName -ine $po -or $l.ListName -ine $name) { throw 'List identity changed.' }
          return $l
        }
        function Find-ListRoute([string]$source) {
          $m = New-Object -ComObject 'MEAOAM.AddressMap'; $m.SourceAddress=$source
          if ($m.GetAddressMap() -ne 1) { return $null }
          if ($m.SourceAddress -ine $source) { throw 'List route identity changed.' }
          return $m
        }
        function Get-ListTarget([string]$po, [string]$address) {
          Assert-ListDomain $po $po
          $route=Find-ListRoute "[SMTP:$address]"
          if ($null -eq $route -or $route.Account -ine $po) { throw 'List route missing or owned by another postoffice.' }
          $prefix="[LS:$po/"
          if (-not $route.DestinationAddress.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or -not $route.DestinationAddress.EndsWith(']')) { throw 'Address is not a list.' }
          $name=$route.DestinationAddress.Substring($prefix.Length, $route.DestinationAddress.Length-$prefix.Length-1)
          if (-not $name -or $name.IndexOfAny([char[]]'\/*?[]') -ge 0) { throw 'Unsafe list identity.' }
          $l=Find-List $po $name
          if ($null -eq $l -or $l.ListAddress -ine "[SMTP:$address]") { throw 'Expected the primary list address.' }
          return $l
        }
        function Read-ListMembers([string]$po, [string]$name) {
          $m=New-Object -ComObject 'MEAOLS.ListMember'; $seen=@{}; $count=0
          $m.AccountName=$po; $m.ListName=$name; $m.Address=''; $m.ListMemberType=-1; $m.Status=-1
          if ($m.FindFirstListMember() -eq 1) {
            do {
              $address=[string]$m.Address
              if ($m.AccountName -ine $po -or $m.ListName -ine $name -or $address -notmatch '^\[SMTP:[^\[\]\r\n]+@[^\[\]\r\n]+\]$' -or $seen.ContainsKey($address)) { throw 'Invalid or repeated list member.' }
              $seen[$address]=$true; $count++
              [pscustomobject]@{ Address=$address; ListMemberType=[int]$m.ListMemberType; Status=[int]$m.Status }
              if ($count -ge 10000) { throw 'List member safety limit reached.' }
              $m.AccountName=$po; $m.ListName=$name; $m.Address=''; $m.ListMemberType=-1; $m.Status=-1
            } while ($m.FindNextListMember() -eq 1)
          }
        }
        function Read-ListRoutes([string]$po, [string]$name) {
          $destination="[LS:$po/$name]"; $seen=@{}; $count=0
          $m=New-Object -ComObject 'MEAOAM.AddressMap'
          $m.Account=$po; $m.SourceAddress=''; $m.DestinationAddress=$destination; $m.Scope=''
          if ($m.FindFirstAddressMap() -eq 1) {
            do {
              $source=[string]$m.SourceAddress
              if ($m.Account -ine $po -or $m.DestinationAddress -ine $destination -or $source -notmatch '^\[SMTP:[^\[\]\r\n]+@[^\[\]\r\n]+\]$' -or $seen.ContainsKey($source)) { throw 'Invalid or repeated list route.' }
              $seen[$source]=$true; $count++
              [pscustomobject]@{ Source=$source; Destination=$destination; Account=$po; Scope=[string]$m.Scope }
              if ($count -ge 20000) { throw 'List route safety limit reached.' }
              $m.Account=$po; $m.SourceAddress=''; $m.DestinationAddress=$destination; $m.Scope=''
            } while ($m.FindNextAddressMap() -eq 1)
          }
        }
        function Add-ListRoute($route) {
          $existing=Find-ListRoute $route.Source
          if ($null -ne $existing) {
            if ($existing.Account -ine $route.Account -or $existing.DestinationAddress -ine $route.Destination) { throw 'Address is already allocated.' }
            return
          }
          $m=New-Object -ComObject 'MEAOAM.AddressMap'
          $m.Account=$route.Account; $m.SourceAddress=$route.Source; $m.DestinationAddress=$route.Destination; $m.Scope=$route.Scope
          if ($m.AddAddressMap() -ne 1) { throw 'List route creation failed.' }
          $check=Find-ListRoute $route.Source
          if ($null -eq $check -or $check.Account -ine $route.Account -or $check.DestinationAddress -ine $route.Destination) { throw 'List route creation readback failed.' }
        }
        function Remove-ListRoute($route) {
          $m=Find-ListRoute $route.Source
          if ($null -eq $m) { return }
          if ($m.Account -ine $route.Account -or $m.DestinationAddress -ine $route.Destination) { throw 'Refusing to remove a foreign route.' }
          if ($m.RemoveAddressMap() -ne 1 -or $null -ne (Find-ListRoute $route.Source)) { throw 'List route removal failed.' }
        }
        function Set-ListMembers([string]$po, [string]$name, [object[]]$wanted) {
          $current=@(Read-ListMembers $po $name); $old=@{}; $next=@{}
          foreach ($entry in $current) { $old[$entry.Address]=$entry }
          foreach ($entry in $wanted) { if ($next.ContainsKey($entry.Address)) { throw 'Duplicate list member.' }; $next[$entry.Address]=$entry }
          foreach ($entry in $wanted) {
            if ($old.ContainsKey($entry.Address)) { continue }
            $m=New-Object -ComObject 'MEAOLS.ListMember'
            $m.AccountName=$po; $m.ListName=$name; $m.Address=$entry.Address; $m.ListMemberType=$entry.ListMemberType; $m.Status=$entry.Status
            if ($m.AddListMember() -ne 1) { throw 'List member creation failed.' }
          }
          foreach ($entry in $current) {
            if ($next.ContainsKey($entry.Address)) { continue }
            $m=New-Object -ComObject 'MEAOLS.ListMember'
            $m.AccountName=$po; $m.ListName=$name; $m.Address=$entry.Address; $m.ListMemberType=$entry.ListMemberType; $m.Status=$entry.Status
            if ($m.RemoveListMember() -ne 1) { throw 'List member removal failed.' }
          }
          $actual=@(Read-ListMembers $po $name)
          if ($actual.Count -ne $next.Count) { throw 'List membership readback failed.' }
          foreach ($entry in $actual) {
            if (-not $next.ContainsKey($entry.Address) -or $entry.Status -ne $next[$entry.Address].Status -or $entry.ListMemberType -ne $next[$entry.Address].ListMemberType) { throw 'List membership readback failed.' }
          }
        }
        function Set-ListPostingMode($l, [int]$mode) {
          # Optional arguments use documented unchanged sentinels, retaining all other settings.
          $rc=$l.EditList($l.Description,$l.AccountName,$l.ListName,$l.ListType,$l.ListStatus,$l.HeaderAnnotationStatus,$l.HeaderAnnotation,$l.FooterAnnotationStatus,$l.FooterAnnotation,$l.ModeratorAddress,$l.ListAddress,-1,'(Nil)',-1,'(Nil)',-1,'(Nil)',-1,'(Nil)','(Nil)',-1,'(Nil)',-1,'(Nil)',-1,-1,$mode)
          $check=Find-List $l.AccountName $l.ListName
          if ($rc -ne 1 -or $null -eq $check -or $check.PostingMode -ne $mode) { throw 'List posting policy readback failed.' }
        }
        """;

    internal static string ReadNativeListsScript(string domain) => NativeListHeader + "\n" + $$"""
        $po='{{PsLiteralRaw(domain)}}'; Assert-ListDomain $po $po
        $rows=@(); $seen=@{}; $count=0
        $l=New-Object -ComObject 'MEAOLS.List'
        $l.AccountName=$po; $l.ListName=''; $l.ListStatus=-1; $l.ListType=-1
        if ($l.FindFirstList() -eq 1) {
          do {
            $name=[string]$l.ListName; $address=[string]$l.ListAddress
            if ($l.AccountName -ine $po -or -not $name -or $name.IndexOfAny([char[]]'\/*?[]') -ge 0 -or $seen.ContainsKey($name)) { throw 'Invalid or repeated list.' }
            $seen[$name]=$true; $count++
            if ($address -notmatch '^\[SMTP:[^\[\]\r\n]+@[^\[\]\r\n]+\]$') { throw 'Invalid primary list address.' }
            if ($address.EndsWith("@$po]",[StringComparison]::OrdinalIgnoreCase)) {
              $members=@(Read-ListMembers $po $name)
              $routes=@(Read-ListRoutes $po $name)
              if (@($routes | Where-Object { $_.Source -ieq $address }).Count -ne 1) { throw 'Primary list route is missing.' }
              $policy=if ($l.ListStatus -ne 1) { 'native-disabled' } elseif ($l.ListType -ne 0) { 'native-moderated' } elseif ($l.PostingMode -eq 1) { 'anyone' } elseif ($l.PostingMode -eq 0) { 'native-members' } elseif ($l.PostingMode -eq 2) { 'native-password' } else { 'native-unknown' }
              $rows += [pscustomobject]@{
                address=$address.Substring(6,$address.Length-7); description=[string]$l.Description; postingPolicy=$policy
                recipients=@($members | ForEach-Object { $_.Address.Substring(6,$_.Address.Length-7) })
                aliases=@($routes | Where-Object { $_.Source -ine $address } | ForEach-Object { $_.Source.Substring(6,$_.Source.Length-7) })
              }
            }
            if ($count -ge 5000) { throw 'List safety limit reached.' }
            $l.AccountName=$po; $l.ListName=''; $l.ListStatus=-1; $l.ListType=-1
          } while ($l.FindNextList() -eq 1)
        }
        [pscustomobject]@{ lists=@($rows) } | ConvertTo-Json -Depth 6 -Compress
        """;

    internal static string CreateNativeListScript(string domain, string local, string description, string[] recipients) => NativeListHeader + "\n" + $$"""
        $po='{{PsLiteralRaw(domain)}}'; $name='{{PsLiteralRaw(local)}}'; $address="$name@$po"
        Assert-ListDomain $po $po
        if ($null -ne (Find-List $po $name) -or $null -ne (Find-ListRoute "[SMTP:$address]")) { throw 'List or address already exists.' }
        $l=New-Object -ComObject 'MEAOLS.List'
        $l.AccountName=$po; $l.ListName=$name; $l.Description='{{PsLiteralRaw(description)}}'
        $l.ListType=0; $l.ListStatus=1; $l.ListAddress="[SMTP:$address]"; $l.ModeratorAddress=''
        $l.HeaderAnnotationStatus=0; $l.HeaderAnnotation=''; $l.FooterAnnotationStatus=0; $l.FooterAnnotation=''
        $l.PostingMode=1; $l.SubscriptionMode=1
        $route=[pscustomobject]@{ Source="[SMTP:$address]"; Destination="[LS:$po/$name]"; Account=$po; Scope='0' }
        $created=$false
        try {
          if ($l.AddList() -ne 1) { throw 'List creation failed.' }
          $created=$true
          $check=Find-List $po $name
          if ($null -eq $check -or $check.ListAddress -ine $l.ListAddress -or $check.Description -cne $l.Description -or $check.ListStatus -ne 1 -or $check.ListType -ne 0 -or $check.PostingMode -ne 1 -or $check.SubscriptionMode -ne 1) { throw 'List creation readback failed.' }
          $wanted=@({{ListPsArray(recipients)}} | ForEach-Object { [pscustomobject]@{ Address="[SMTP:$_]"; ListMemberType=0; Status=1 } })
          Set-ListMembers $po $name $wanted
          # Activate the route only after the list and its members are verified.
          Add-ListRoute $route
        } catch {
          if (-not $created) { throw 'List creation was rejected; inspect the list configuration.' }
          try {
            Remove-ListRoute $route
            $rollback=Find-List $po $name
            if ($null -ne $rollback) {
              Set-ListMembers $po $name @()
              if ($rollback.RemoveList() -ne 1 -or $null -ne (Find-List $po $name)) { throw 'Rollback list removal failed.' }
            }
          } catch { throw 'List creation failed and rollback could not be verified; inspect the list configuration.' }
          throw 'List creation failed; prior state restored.'
        }
        'OK'
        """;

    internal static string UpdateNativeListScript(string domain, string address, string[]? replace, string[] add, string[] remove, bool setAnyone) => NativeListHeader + "\n" + $$"""
        $po='{{PsLiteralRaw(domain)}}'; $address='{{PsLiteralRaw(address)}}'
        $l=Get-ListTarget $po $address; $name=[string]$l.ListName
        $before=@(Read-ListMembers $po $name); $routes=@(Read-ListRoutes $po $name)
        $next=@{}
        {{(replace is null ? "foreach ($entry in $before) { $next[$entry.Address]=$entry }" : "foreach ($value in " + ListPsArray(replace) + ") { $next[\"[SMTP:$value]\"]=[pscustomobject]@{ Address=\"[SMTP:$value]\"; ListMemberType=0; Status=1 } }")}}
        foreach ($value in {{ListPsArray(add)}}) {
          $key="[SMTP:$value]"
          if (-not $next.ContainsKey($key)) { $next[$key]=[pscustomobject]@{ Address=$key; ListMemberType=0; Status=1 } }
        }
        foreach ($value in {{ListPsArray(remove)}}) { $next.Remove("[SMTP:$value]") }
        foreach ($route in $routes) { if ($next.ContainsKey($route.Source)) { throw 'A list cannot contain its own address or alias.' } }
        $oldMode=[int]$l.PostingMode; $changeMode={{(setAnyone ? "$true" : "$false")}}
        try {
          Set-ListMembers $po $name @($next.Values)
          if ($changeMode -and $oldMode -ne 1) { Set-ListPostingMode $l 1 }
        } catch {
          try {
            Set-ListMembers $po $name $before
            if ($changeMode) { Set-ListPostingMode $l $oldMode }
          } catch { throw 'List update failed and rollback could not be verified; inspect the list configuration.' }
          throw 'List update failed; prior state restored.'
        }
        'OK'
        """;

    internal static string ListAliasScript(string domain, string address, string aliasDomain, string alias, bool add) => NativeListHeader + "\n" + $$"""
        $po='{{PsLiteralRaw(domain)}}'; $address='{{PsLiteralRaw(address)}}'; $alias='{{PsLiteralRaw(alias)}}'
        $l=Get-ListTarget $po $address; $name=[string]$l.ListName
        Assert-ListDomain '{{PsLiteralRaw(aliasDomain)}}' $po
        $route=[pscustomobject]@{ Source="[SMTP:$alias]"; Destination="[LS:$po/$name]"; Account=$po; Scope='0' }
        $previous=Find-ListRoute $route.Source
        if ($alias -ieq $address) { throw 'The primary list address cannot be changed as an alias.' }
        $adding={{(add ? "$true" : "$false")}}
        if ($adding) {
          if ($null -ne $previous) { throw 'Alias address is already allocated.' }
          if (@(Read-ListMembers $po $name | Where-Object { $_.Address -ieq $route.Source }).Count -ne 0) { throw 'Alias would create a list delivery loop.' }
        } else {
          if ($null -eq $previous -or $previous.Account -ine $po -or $previous.DestinationAddress -ine $route.Destination) { throw 'Alias does not belong to this list.' }
          $route.Scope=[string]$previous.Scope
        }
        try {
          if ($adding) { Add-ListRoute $route } else { Remove-ListRoute $route }
        } catch {
          try { if ($adding) { Remove-ListRoute $route } else { Add-ListRoute $route } }
          catch { throw 'List alias update failed and rollback could not be verified.' }
          throw 'List alias update failed; prior state restored.'
        }
        'OK'
        """;

    internal static string DeleteNativeListScript(string domain, string address) => NativeListHeader + "\n" + $$"""
        $po='{{PsLiteralRaw(domain)}}'; $address='{{PsLiteralRaw(address)}}'
        $original=Get-ListTarget $po $address; $name=[string]$original.ListName
        $members=@(Read-ListMembers $po $name); $routes=@(Read-ListRoutes $po $name)
        if ($routes.Count -eq 0) { throw 'List routes could not be measured.' }
        try {
          foreach ($route in $routes) { Remove-ListRoute $route }
          Set-ListMembers $po $name @()
          $deleting=Find-List $po $name
          if ($null -eq $deleting -or $deleting.RemoveList() -ne 1 -or $null -ne (Find-List $po $name)) { throw 'List deletion readback failed.' }
        } catch {
          try {
            if ($null -eq (Find-List $po $name)) {
              # Retain all native-only fields in the original COM object, never in logs.
              if ($original.AddList() -ne 1) { throw 'List restoration failed.' }
            }
            Set-ListMembers $po $name $members
            foreach ($route in $routes) { Add-ListRoute $route }
          } catch { throw 'List deletion failed and rollback could not be verified; inspect the list configuration.' }
          throw 'List deletion failed; prior state restored.'
        }
        'OK'
        """;
}
