using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Assets.Scripts
{
    public class UtilityMethods
    {
        /// <summary>
        /// Adds a header to a file as its first line, by creating a temporary file, writing the header, and copying over the permanent file's contents line by line.
        /// If the file at <paramref name="filename"/> does not exist, it will be created, only containing a single line made up of <paramref name="header"/>.
        /// </summary>
        /// <param name="header">The header to write to the first line of the file</param>
        /// <param name="filename">The path to the permanent file in which to write the header</param>
        public static void AddFirstLine(string header, string filename)
        {
            string tempfile = Path.Combine(Application.persistentDataPath, "temp.tmp");
            
            using (var writer = new StreamWriter(tempfile))
            {
                writer.WriteLine(header);

                if (File.Exists(filename))
                {
                    using var reader = new StreamReader(filename);
                    while (!reader.EndOfStream)
                        writer.WriteLine(reader.ReadLine());
                }
            }
            File.Delete(filename);
            File.Copy(tempfile, filename);
            File.Delete(tempfile);
        }

        /// <summary>
        /// Creates and initializes a list containing all integers from <paramref name="startIndex"/> to <paramref name="endIndex"/> in ascending order.
        /// </summary>
        /// <returns>A list containing all integers from <paramref name="startIndex"/> to <paramref name="endIndex"/> in ascending order.</returns>
        public static List<int> CreateAndInitializeList(int startIndex, int endIndex)
        {
            if (endIndex < startIndex)
            {
                throw new System.ArgumentException($"Ending index {endIndex} cannot be smaller than starting index {startIndex}.");
            }

            List<int> list = new List<int>(endIndex - startIndex);
            for (int i = startIndex; i < endIndex; i++)
            {
                list.Add(i);
            }
            return list;
        }

        /// <summary>
        /// Shuffles a copy of a list using <see cref="ShuffleList(List{int})"/>, then only takes the first <paramref name="count"/> elements.
        /// </summary>
        /// <param name="list">The list to copy, shuffle and filter.</param>
        /// <param name="count">The number of elements to keep in the list.</param>
        /// <returns>A shuffled copy of list <paramref name="list"/> in undefined random order containing <paramref name="count"/> elements.</returns>
        /// <exception cref="System.ArgumentException">In case <paramref name="count"/> is greater than the number of elements inside <paramref name="list"/></exception>
        public static List<int> ShuffleAndFilterList(List<int> list, int count)
        {
            if (count > list.Count)
                throw new System.ArgumentException($"[ERROR] Invalid elements number {count} for list with only {list.Count} elements.");
            else {
                List<int> listCopy = new List<int>(list);
                ShuffleList(listCopy);
                if (count < list.Count)
                {
                    listCopy = listCopy.GetRange(0, count);
                }
                return listCopy;
            }   
        }

        /// <summary>
        /// Shuffles a list using the Fisher-Yates algorithm
        /// </summary>
        /// <param name="list">The list to shuffle.</param>
        private static void ShuffleList(List<int> list)
        {
            int n = list.Count;
            while (n > 1)
            {
                n--;
                int k = UnityEngine.Random.Range(0, n + 1);
                int value = list[k];
                list[k] = list[n];
                list[n] = value;
            }
        }

        /// <summary>
        /// Reads, removes and then returns the first (i.e. 0-th) element of a list.
        /// </summary>
        /// <param name="list">The list of which to read the head.</param>
        /// <returns>The first element of the list, which was removed.</returns>
        public static int ReadAndRemoveHead(List<int> list)
        {
            int element = list[0];
            list.RemoveAt(0);
            return element;
        }

        public static Vector3 Divide(in Vector3 a, Vector3 b)
        {
            Vector3 result = new Vector3(
                a.x / b.x,
                a.y / b.y,
                a.z / b.z
            );
            return result;
        }

        public void ScalePrefab(GameObject model, Vector3 modelScale, Vector3 targetScale, float scale, float EPSILON = 1e-05f)
        {
            var xFraction = modelScale.x / targetScale.x;
            var yFraction = modelScale.y / targetScale.y;
            var zFraction = modelScale.z / targetScale.z;

            Vector3 fraction = new Vector3(xFraction, yFraction, zFraction);
            // Check if any of the fraction's features is "zero", if so avoid division (because dividing by 0 is bad m'kay?)
            if (fraction.x < EPSILON)
            {
                fraction.x = 1.0f;
            }
            if (fraction.y < EPSILON)
            {
                fraction.y = 1.0f;
            }
            if (fraction.z < EPSILON)
            {
                fraction.z = 1.0f;
            }

            model.transform.localScale = Divide(model.transform.localScale, fraction) * scale;
        }

        /// <summary>
        /// Asynchronously creates a spatial anchor and invokes a callback upon successful creation.
        /// </summary>
        /// <param name="onAnchorCreated">The action to perform with the created anchor. The anchor is passed as a parameter.</param>
        public static IEnumerator CreateSpatialAnchorWithCallback(Action<OVRSpatialAnchor> onAnchorCreated)
        {
            var go = new GameObject("My Persistent Anchor");
            var anchor = go.AddComponent<OVRSpatialAnchor>();

            // Wait for the async creation
            yield return new WaitUntil(() => anchor.Created);

            // Check if the anchor was successfully created before invoking the callback
            if (anchor.Created)
            {
                Debug.Log($"Successfully created anchor {anchor.Uuid}");
                // Invoke the callback action and pass the anchor to it.
                onAnchorCreated?.Invoke(anchor);
            }
            else
            {
                Debug.LogError("Failed to create spatial anchor.");
                // It's good practice to handle failure. We can pass null to the callback.
                onAnchorCreated?.Invoke(null);
            }
        }
    }
}