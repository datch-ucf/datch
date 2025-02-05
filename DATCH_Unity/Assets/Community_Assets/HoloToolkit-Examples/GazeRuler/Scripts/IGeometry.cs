// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using UnityEngine;
using System.Collections;

namespace HoloToolkit.Examples.GazeRuler
{
    /// <summary>
    /// interface for geometry class
    /// </summary>
    public interface IGeometry
    {
        void AddPoint(GameObject LinePrefab, GameObject PointPrefab, GameObject TextPrefab, bool loadedPoint = false, Vector3 pointPosition = new Vector3());

        void Delete();

        void Clear();

        void Reset();

        void UndoLine();
    }
}