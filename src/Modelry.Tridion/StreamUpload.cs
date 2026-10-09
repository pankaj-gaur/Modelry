#if CORESERVICE_PROXY
using System.ServiceModel;
using CS = Tridion.ContentManager.CoreService.Client;

namespace Modelry.Tridion;

// Client for the Core Service upload endpoint, written from the CMS's own WSDL (CoreService201701.svc?singleWsdl):
//   port      streamUpload_basicHttp   (…/CoreService201701.svc/streamUpload_basicHttp)
//   contract  IStreamUpload, namespace http://www.sdltridion.com/ContentManager/CoreService/201701
//   policy    OptimizedMimeSerialization (MTOM), no HTTP authentication
//   operation UploadBinaryByteArray(accessToken, data) → UploadBinaryByteArrayResult: the server-side file path that
//             BinaryContentData.UploadFromFile then points at.
//   accessToken is mandatory ("Value cannot be null. Parameter name: accessToken"): it is the AccessTokenData that
//             ICoreService.GetCurrentUser returns on the authenticated main endpoint, signed by the CMS – the upload
//             endpoint itself is anonymous and trusts that signature instead.
[ServiceContract(Namespace = StreamUploadContract.Ns, Name = "IStreamUpload")]
internal interface IStreamUpload
{
    [OperationContract(Action = StreamUploadContract.Ns + "/IStreamUpload/UploadBinaryByteArray",
                       ReplyAction = StreamUploadContract.Ns + "/IStreamUpload/UploadBinaryByteArrayResponse")]
    Task<string> UploadBinaryByteArrayAsync(CS.AccessTokenData accessToken, byte[] data);
}

internal static class StreamUploadContract
{
    public const string Ns = "http://www.sdltridion.com/ContentManager/CoreService/201701";
}
#endif
