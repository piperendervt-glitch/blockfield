using System.Runtime.CompilerServices;

// 格子の主軸合わせ（FindBestYaw / RotateAroundY / HorizontalArea）は幾何計算なので
// テストを付けたい。ただし public にすると外から呼べる API になってしまい、
// 「焼き込みの内部手順」であることが伝わらない。
// internal のままテストへ公開する。
[assembly: InternalsVisibleTo("BlockField.Tests.EditMode")]
