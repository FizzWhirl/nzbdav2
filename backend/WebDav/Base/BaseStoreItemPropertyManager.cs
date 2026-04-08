using NWebDav.Server.Props;
using NzbWebDAV.Utils;

namespace NzbWebDAV.WebDav.Base;

public class BaseStoreItemPropertyManager() : PropertyManager<BaseStoreItem>(DavProperties)
{
    private static readonly DavProperty<BaseStoreItem>[] DavProperties =
    [
        new DavDisplayName<BaseStoreItem>
        {
            Getter = item => item.Name
        },
        new DavGetContentLength<BaseStoreItem>
        {
            Getter = item => item.FileSize
        },
        new DavGetContentType<BaseStoreItem>
        {
            Getter = item => ContentTypeUtil.GetContentType(item.Name)
        },
        new DavGetLastModified<BaseStoreItem>
        {
            Getter = x => x.CreatedAt
        },
        new Win32FileAttributes<BaseStoreItem>
        {
            Getter = _ => FileAttributes.Normal
        }
    ];

    public static readonly BaseStoreItemPropertyManager Instance = new();
}