﻿using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using VRage.Generics;

using VRageMath;
using VRageRender.Resources;
using VRageRender.Vertex;
using Buffer = SharpDX.Direct3D11.Buffer;
using Matrix = VRageMath.Matrix;
using Vector3 = VRageMath.Vector3;
using Vector4 = VRageMath.Vector4;
using BoundingBox = VRageMath.BoundingBox;
using BoundingFrustum = VRageMath.BoundingFrustum;
using VRage.Collections;
using System.Collections.Specialized;
using System.Threading;
using VRageMath.PackedVector;


namespace VRageRender
{

    struct InstancingId
    {
        internal int Index;

        public static bool operator ==(InstancingId x, InstancingId y)
        {
            return x.Index == y.Index;
        }

        public static bool operator !=(InstancingId x, InstancingId y)
        {
            return x.Index != y.Index;
        }

        internal static readonly InstancingId NULL = new InstancingId { Index = -1 };

        internal MyInstancingInfo Info { get { return MyInstancing.Instancings.Data[Index]; } }

        internal VertexBufferId VB { get { return MyInstancing.Data[Index].VB; } }
    }

    struct MyInstancingInfo
    {
        internal MyRenderInstanceBufferType Type;
        internal VertexLayoutId Layout;
        internal int Capacity;
        internal int Stride;
        internal string DebugName;
        internal byte[] Data;
    }

    struct MyInstancingData
    {
        internal VertexBufferId VB;
    }

    static class MyInstancing
    {
        static Dictionary<uint, InstancingId> IdIndex = new Dictionary<uint, InstancingId>();
        internal static MyFreelist<MyInstancingInfo> Instancings = new MyFreelist<MyInstancingInfo>(128);
        internal static MyInstancingData[] Data = new MyInstancingData[128];

        internal static InstancingId Get(uint GID)
        {
            return IdIndex.Get(GID, InstancingId.NULL);
        }

        internal unsafe static InstancingId Create(uint GID, MyRenderInstanceBufferType type, string debugName)
        {
            var id = new InstancingId { Index = Instancings.Allocate() };

            Instancings.Data[id.Index] = new MyInstancingInfo
            {
                Type = type,
                DebugName = debugName
            };

            MyArrayHelpers.Reserve(ref Data, id.Index + 1);
            Data[id.Index] = new MyInstancingData
            {
                VB = VertexBufferId.NULL
            };

            if(type == MyRenderInstanceBufferType.Cube)
            {
                Instancings.Data[id.Index].Layout = MyVertexLayouts.GetLayout(new MyVertexInputComponent(MyVertexInputComponentType.CUBE_INSTANCE, 2, MyVertexInputComponentFreq.PER_INSTANCE));
                Instancings.Data[id.Index].Stride = sizeof(MyVertexFormatCubeInstance);
            }
            else
            {
                Instancings.Data[id.Index].Layout = MyVertexLayouts.GetLayout(new MyVertexInputComponent(MyVertexInputComponentType.GENERIC_INSTANCE, 2, MyVertexInputComponentFreq.PER_INSTANCE));
                Instancings.Data[id.Index].Stride = sizeof(MyVertexFormatGenericInstance);
            }

            IdIndex[GID] = id;

            return id;
        }

        internal static void RemoveResource(InstancingId id)
        {
            if(Data[id.Index].VB != VertexBufferId.NULL)
            {
                MyHwBuffers.Destroy(Data[id.Index].VB);
                Data[id.Index].VB = VertexBufferId.NULL;
            }
        }

        internal static void Remove(uint GID, InstancingId id)
        {
            RemoveResource(id);
            IdIndex.Remove(GID);
            Instancings.Data[id.Index] = new MyInstancingInfo { };
            Instancings.Free(id.Index);            
        }

        internal static void OnSessionEnd()
        {
            foreach(var id in IdIndex.ToArray())
            {
                Remove(id.Key, id.Value);
            }
        }

        internal static unsafe void UpdateGeneric(InstancingId id, List<MyInstanceData> instanceData, int capacity)
        {
            Debug.Assert(id.Info.Type == MyRenderInstanceBufferType.Generic);

            var info = id.Info;

            var byteSize = info.Stride * capacity;

            if(Instancings.Data[id.Index].Data == null)
            {
                Instancings.Data[id.Index].Data = new byte[byteSize];
            }
            else
            {
                MyArrayHelpers.Reserve(ref Instancings.Data[id.Index].Data, byteSize);
            }
            
            var list = instanceData.ToArray();

            fixed(void *src = list)
            {
                fixed (void* dst = Instancings.Data[id.Index].Data)
                {
                    SharpDX.Utilities.CopyMemory(new IntPtr(dst), new IntPtr(src), info.Stride * list.Length);
                }
            }

            Instancings.Data[id.Index].Capacity = capacity;
            UpdateVertexBuffer(id);
        }

        internal static unsafe void UpdateCube(InstancingId id, List<MyCubeInstanceData> instanceData, int capacity)
        {
            Debug.Assert(id.Info.Type == MyRenderInstanceBufferType.Cube);

            var info = id.Info;

            var byteSize = info.Stride * capacity;

            if (Instancings.Data[id.Index].Data == null)
            {
                Instancings.Data[id.Index].Data = new byte[byteSize];
            }
            else
            {
                MyArrayHelpers.Reserve(ref Instancings.Data[id.Index].Data, byteSize);
            }

            //var rawBuffer = new MyVertexFormatCubeInstance[m_capacity];

            fixed (void* dst = Instancings.Data[id.Index].Data)
            {
                MyVertexFormatCubeInstance* ptr = (MyVertexFormatCubeInstance*) dst;

                for (int i = 0; i < instanceData.Count; i++)
                {
                    fixed (byte* pSource = instanceData[i].RawBones())
                    {
                        for (int j = 0; j < MyRender11Constants.CUBE_INSTANCE_BONES_NUM * 4; j++)
                            ptr[i].bones[j] = pSource[j];
                    }
                    ptr[i].translationRotation = new HalfVector4(instanceData[i].m_translationAndRot);

                    var colorMaskHSV = instanceData[i].ColorMaskHSV;
                    //Vector3 color = MyRender11.ColorFromMask(new Vector3(colorMaskHSV.X, colorMaskHSV.Y, colorMaskHSV.Z));
                    ptr[i].colorMaskHSV = new HalfVector4(colorMaskHSV);
                    
                }
            }
            

            Instancings.Data[id.Index].Capacity = capacity;
            UpdateVertexBuffer(id);
        }

        internal unsafe static void UpdateVertexBuffer(InstancingId id)
        {
            var info = id.Info;
            if (info.Capacity == 0)
            { 
                return;
            }

            fixed (byte* ptr = info.Data)
            {
                if(Data[id.Index].VB == VertexBufferId.NULL)
                {
                    Data[id.Index].VB = MyHwBuffers.CreateVertexBuffer(info.Capacity, info.Stride, new IntPtr(ptr), info.DebugName);
                }
                else
                {
                    var vb = Data[id.Index].VB;
                    MyHwBuffers.ResizeVertexBuffer(vb, info.Capacity);

                    DataBox srcBox = new DataBox(new IntPtr(ptr));
                    ResourceRegion dstRegion = new ResourceRegion(0, 0, 0, info.Stride * info.Capacity, 1, 1);

                    MyRender11.ImmediateContext.UpdateSubresource(srcBox, vb.Buffer, 0, dstRegion);
                }
            }
        }

        internal static void OnDeviceReset()
        {
            foreach(var id in IdIndex.Values)
            {
                RemoveResource(id);
                UpdateVertexBuffer(id);
            }
        }
    }

    class MyInstancingComponent : MyActorComponent
    {
        private string m_debugName;

        // one-to-many component type -> generalize if more
        List<MyActor> m_owners;
        MyIDTracker<MyInstancingComponent> m_ID;
        internal VertexBufferId VB;
        internal MyVertexInputLayout m_input;
        internal int m_stride;
        internal MyRenderInstanceBufferType m_type;

        int m_capacity;

        internal override void Construct()
        {
            base.Construct();
            Type = MyActorComponentEnum.Instancing;


            m_capacity = 0;
            m_input = MyVertexInputLayout.Empty();
            m_ID = new MyIDTracker<MyInstancingComponent>();
            VB = VertexBufferId.NULL;
            
            if(m_owners == null)
            {
                m_owners = new List<MyActor>();
            }
            else
            {
                m_owners.Clear();
            }
        }

        internal override void Destruct()
        {
            if (m_ID.Value != null)
            {
                m_ID.Deregister();
            }

            Dispose();

            base.Destruct();
        }

        internal override void Assign(MyActor owner)
        {
            Debug.Assert(m_owners.Find(x => x == owner) == null);
            base.Assign(owner);

            m_owners.Add(owner);
            owner.MarkRenderDirty();
        }

        internal unsafe void Init(MyRenderInstanceBufferType type)
        {
            if (type == MyRenderInstanceBufferType.Cube)
            {
                m_input = m_input.Append(MyVertexInputComponentType.CUBE_INSTANCE, 2, MyVertexInputComponentFreq.PER_INSTANCE);
                m_stride = sizeof(MyVertexFormatCubeInstance);
            }
            else if (type == MyRenderInstanceBufferType.Generic)
            {
                m_input = m_input.Append(MyVertexInputComponentType.GENERIC_INSTANCE, 2, MyVertexInputComponentFreq.PER_INSTANCE);
                m_stride = sizeof(MyVertexFormatGenericInstance);
            }

            m_type = type;
        }

        internal void Dispose()
        {
            if (VB != VertexBufferId.NULL)
            {
                MyHwBuffers.Destroy(VB);
                VB = VertexBufferId.NULL;
            }
            m_capacity = 0;
        }

        internal unsafe void UpdateGeneric(List<MyInstanceData> instanceData, int capacity)
        {
            Debug.Assert(m_type == MyRenderInstanceBufferType.Generic);

            var instancesNum = instanceData.Count;
            if (m_capacity < instancesNum && VB != VertexBufferId.NULL)
            {
                MyHwBuffers.Destroy(VB);
                VB = VertexBufferId.NULL;
            }
            if (m_capacity < instancesNum)
            {
                m_capacity = Math.Max(instancesNum, capacity);
                VB = MyHwBuffers.CreateVertexBuffer(m_capacity, sizeof(MyVertexFormatGenericInstance), null, m_debugName + " instances buffer");
            }

            fixed (MyInstanceData* dataPtr = instanceData.ToArray())
            {
                DataBox srcBox = new DataBox(new IntPtr(dataPtr));
                ResourceRegion dstRegion = new ResourceRegion(0, 0, 0, sizeof(MyVertexFormatGenericInstance) * instancesNum, 1, 1);

                MyRender11.ImmediateContext.UpdateSubresource(srcBox, VB.Buffer, 0, dstRegion);
            }

            BumpRenderable();
        }

        internal void BumpRenderable()
        {
            foreach(var owner in m_owners)
            {
                owner.MarkRenderDirty();
            }
        }

        internal unsafe void UpdateCube(List<MyCubeInstanceData> instanceData, int capacity)
        {
            Debug.Assert(m_type == MyRenderInstanceBufferType.Cube);

            var instancesNum = instanceData.Count;
            if (m_capacity < instancesNum && VB != VertexBufferId.NULL)
            {
                MyHwBuffers.Destroy(VB);
                VB = VertexBufferId.NULL;
            }
            if (m_capacity < instancesNum)
            {
                m_capacity = Math.Max(instancesNum, capacity);
                VB = MyHwBuffers.CreateVertexBuffer(m_capacity, sizeof(MyVertexFormatCubeInstance), null, m_debugName + " instances buffer");
            }

            var rawBuffer = new MyVertexFormatCubeInstance[m_capacity];
            for (int i = 0; i < instancesNum; i++)
            {
                fixed (byte* pSource = instanceData[i].RawBones(), pTarget = rawBuffer[i].bones)
                {
                    for (int j = 0; j < MyRender11Constants.CUBE_INSTANCE_BONES_NUM * 4; j++)
                        pTarget[j] = pSource[j];
                }
                rawBuffer[i].translationRotation = new HalfVector4(instanceData[i].m_translationAndRot);
                rawBuffer[i].colorMaskHSV = new HalfVector4(instanceData[i].ColorMaskHSV);
            }

            fixed (MyVertexFormatCubeInstance* dataPtr = rawBuffer)
            {
                DataBox srcBox = new DataBox(new IntPtr(dataPtr));
                ResourceRegion dstRegion = new ResourceRegion(0, 0, 0, sizeof(MyVertexFormatCubeInstance) * instancesNum, 1, 1);

                MyRender11.ImmediateContext.UpdateSubresource(srcBox, VB.Buffer, 0, dstRegion);
            }

            BumpRenderable();
        }

        internal void SetDebugName(string name)
        {
            m_debugName = name;
        }

        internal void SetID(uint id)
        {
            m_ID.Register(id, this);
        }

        internal override void OnRemove(MyActor owner)
        {
            base.OnRemove(owner);

            m_owners.Remove(owner);
        }
    }
}
