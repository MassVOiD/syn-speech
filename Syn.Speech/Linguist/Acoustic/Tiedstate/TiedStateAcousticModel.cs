﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using Syn.Logging;
using Syn.Speech.Helper;
using Syn.Speech.Util.Props;
//PATROLLED
namespace Syn.Speech.Linguist.Acoustic.Tiedstate
{
    /// <summary>
    /// 
    /// Loads a tied-state acoustic model generated by the Sphinx-3 trainer.
    /// <p/>
    /// <p/>
    /// It is not the goal of this documentation to provide an explanation about the concept of HMMs. The explanation below
    /// is superficial, and provided only in a way that the files in the acoustic model package make sense.
    /// <p/>
    /// An HMM models a process using a sequence of states. Associated with each state, there is a probability density
    /// function. A popular choice for this function is a Gaussian mixture, that is, a summation of Gaussians. As you may
    /// recall, a single Gaussian is defined by a mean and a variance, or, in the case of a multidimensional Gaussian, by a
    /// mean vector and a covariance matrix, or, under some simplifying assumptions, a variance vector. The "means" and
    /// "variances" files in the "continuous" directory contain exactly this: a table in which each line contains a mean
    /// vector or a variance vector respectively. The dimension of these vectors is the same as the incoming data, the
    /// encoded speech signal. The Gaussian mixture is a summation of Gaussians, with different weights for different
    /// Gaussians. The "mixture_weights" file contains this: each line contains the weights for a combination of Gaussians.
    /// <p/>
    /// The HMM is a model with a set of states. The transitions between states have an associated probability. These
    /// probabilities make up the transition matrices stored in the "transition_matrices" file.
    /// <p/>
    /// The files in the "continuous" directory are, therefore, tables, or pools, of means, variances, mixture weights, and
    /// transition probabilities.
    /// <p/>
    /// The dictionary is a file that maps words to their phonetic transcriptions, that is, it maps words to sequences of
    /// phonemes.
    /// <p/>
    /// The language model contains information about probabilities of words in a language. These probabilities could be for
    /// individual words or for sequences of two or three words.
    /// <p/>
    /// The model definition file in a way ties everything together. If the recognition system models phonemes, there is an
    /// HMM for each phoneme. The model definition file has one line for each phoneme. The phoneme could be in a context
    /// dependent or independent. Each line, therefore, identifies a unique HMM. This line has the phoneme identification,
    /// the non-required left or right context, the index of a transition matrix, and, for each state, the index of a mean
    /// vector, a variance vector, and a set of mixture weights.
    /// </summary>
    public class TiedStateAcousticModel:AcousticModel
    {
        /** The property that defines the component used to load the acoustic model */
        [S4Component(Type = typeof(ILoader))]
        public static string PropLoader = "loader";

        /** The property that defines the unit manager */
        [S4Component(Type = typeof(UnitManager))]
        public static string PropUnitManager = "unitManager";

        /** Controls whether we generate composites or CI units when no context is given during a lookup. */
        [S4Boolean(DefaultValue = true)]
        public static string PropUseComposites = "useComposites";


        // -----------------------------
        // Configured variables
        // -----------------------------
        private string _name;
        protected ILoader Loader;
        protected UnitManager UnitManager;
        private Boolean _useComposites;
        private JProperties[] properties;

       
        // ----------------------------
        // internal variables
        // -----------------------------
        private readonly HashMap<String, SenoneSequence> _compositeSenoneSequenceCache = new HashMap<String, SenoneSequence>();
        private Boolean _allocated;

        public TiedStateAcousticModel(ILoader loader, UnitManager unitManager, Boolean useComposites, JProperties[] properties) 
        {
            Loader = loader;
            UnitManager = unitManager;
            _useComposites = useComposites;
            this.properties = properties;
        }

        public TiedStateAcousticModel()
        {

        }

        public override void NewProperties(PropertySheet ps)
        {
            Loader = (ILoader) ps.GetComponent(PropLoader);
            UnitManager = (UnitManager) ps.GetComponent(PropUnitManager);
            _useComposites = ps.GetBoolean(PropUseComposites);
        }

        /**
        /// initialize this acoustic model with the given name and context.
         *
        /// @throws IOException if the model could not be loaded
         */
        public override void Allocate()
        {
            if (!_allocated) {
                Loader.Load();
                LogInfo();
                _allocated = true;
            }
        }


        /* (non-Javadoc)
       /// @see edu.cmu.sphinx.linguist.acoustic.AcousticModel#deallocate()
        */
        public override void Deallocate() {
        }


        /**
        /// Returns the name of this AcousticModel, or null if it has no name.
         *
        /// @return the name of this AcousticModel, or null if it has no name
         */

        public override string Name
        {
            get { return _name; }
        }


        /**
        /// Gets a composite HMM for the given unit and context
         *
        /// @param unit     the unit for the hmm
        /// @param position the position of the unit within the word
        /// @return a composite HMM
         */
        private IHMM GetCompositeHMM(Unit unit, HMMPosition position) 
        {

            var ciUnit = UnitManager.GetUnit(unit.Name, unit.IsFiller,
                    Context.EmptyContext);

            var compositeSequence = GetCompositeSenoneSequence(unit,
                    position);

            var contextIndependentHMM = (SenoneHMM) LookupNearestHMM(ciUnit,
                    HMMPosition.Undefined, true);
            var tmat = contextIndependentHMM.TransitionMatrix;
            return new SenoneHMM(unit, compositeSequence, tmat, position);
        }


        /**
        /// Given a unit, returns the HMM that best matches the given unit. If exactMatch is false and an exact match is not
        /// found, then different word positions are used. If any of the contexts are non-silence filler units. a silence
        /// filler unit is tried instead.
         *
        /// @param unit       the unit of interest
        /// @param position   the position of the unit of interest
        /// @param exactMatch if true, only an exact match is acceptable.
        /// @return the HMM that best matches, or null if no match could be found.
         */
        public override IHMM LookupNearestHMM(Unit unit, HMMPosition position,Boolean exactMatch) 
        {

            if (exactMatch)
                return lookupHMM(unit, position);

            var mgr = Loader.HMMManager;
            var hmm = mgr.Get(position, unit);

            if (hmm != null) {
                return hmm;
            }
            // no match, try a composite

            if (_useComposites && hmm == null) {
                if (IsComposite(unit)) {

                    hmm = GetCompositeHMM(unit, position);
                    if (hmm != null) {
                        mgr.Put(hmm);
                    }
                }
            }
            // no match, try at other positions
            if (hmm == null) {
                hmm = GetHMMAtAnyPosition(unit);
            }
            // still no match, try different filler
            if (hmm == null) {
                hmm = GetHMMInSilenceContext(unit, position);
            }

            // still no match, backoff to base phone
            if (hmm == null) {
                var ciUnit = lookupUnit(unit.Name);

                Debug.Assert(unit.IsContextDependent());
                if (ciUnit == null) {
                    this.LogInfo("Can't find HMM for " + unit.Name);
                }
                Debug.Assert(ciUnit != null);
                Debug.Assert(!ciUnit.IsContextDependent());

                hmm = mgr.Get(HMMPosition.Undefined, ciUnit);
            }

            Debug.Assert(hmm != null);

            // System.out.println("PROX match for "
            // 	+ unit + " at " + position + ":" + hmm);

            return hmm;
        }


        /**
        /// Determines if a unit is a composite unit
         *
        /// @param unit the unit to test
        /// @return true if the unit is missing a right context
         */
        private Boolean IsComposite(Unit unit) 
        {

            if (unit.IsFiller) {
                return false;
            }

            var context = unit.Context;
            if (context is LeftRightContext) 
            {
                var lrContext = (LeftRightContext) context;
                if (lrContext.RightContext == null) {
                    return true;
                }
                if (lrContext.LeftContext == null) {
                    return true;
                }
            }
            return false;
        }


        /**
        /// Looks up the context independent unit given the name
         *
        /// @param name the name of the unit
        /// @return the unit or null if the unit was not found
         */
        private Unit lookupUnit(String name) 
        {
            return Loader.ContextIndependentUnits.Get(name);
        }


        /**
        /// Returns an iterator that can be used to iterate through all the HMMs of the acoustic model
         *
        /// @return an iterator that can be used to iterate through all HMMs in the model. The iterator returns objects of
        ///         type <code>HMM</code>.
         */
        public override IEnumerator<IHMM> GetHMMIterator() 
        {
            return Loader.HMMManager.GetEnumerator();
        }


        /**
        /// Returns an iterator that can be used to iterate through all the CI units in the acoustic model
         *
        /// @return an iterator that can be used to iterate through all CI units. The iterator returns objects of type
        ///         <code>Unit</code>
         */
        public override IEnumerator<Unit> GetContextIndependentUnitIterator() 
        {
            return Loader.ContextIndependentUnits.Values.GetEnumerator();
        }


        /**
        /// Get a composite senone sequence given the unit.
         *
        /// The unit should have a LeftRightContext, where one or two of 'left' or
        /// 'right' may be null to indicate that the match should succeed on any
        /// context.
         *
        /// @param unit the unit
         */
        public SenoneSequence GetCompositeSenoneSequence(Unit unit,HMMPosition position)
        {
            var unitStr = unit.ToString();
            var compositeSenoneSequence = _compositeSenoneSequenceCache.Get(unitStr);


            this.LogInfo("getCompositeSenoneSequence: "
                            + unit +
                            compositeSenoneSequence == null ? "" : "Cached");

            if (compositeSenoneSequence != null)
                return compositeSenoneSequence;

            // Iterate through all HMMs looking for
            // a) An hmm with a unit that has the proper base
            // b) matches the non-null context

            var context = unit.Context;
            List<SenoneSequence> senoneSequenceList;
            senoneSequenceList = new List<SenoneSequence>();

            // collect all senone sequences that match the pattern
            for (var i = GetHMMIterator(); i.MoveNext();) 
            {
                var hmm = (SenoneHMM) i.Current;
                if (hmm.Position == position) {
                    var hmmUnit = hmm.Unit;
                    if (hmmUnit.IsPartialMatch(unit.Name, context)) 
                    {
                         this.LogInfo("collected: " + hmm.Unit);

                        senoneSequenceList.Add(hmm.SenoneSequence);
                    }
                }
            }

            // couldn't find any matches, so at least include the CI unit
            if (senoneSequenceList.Count==0) 
            {
                var ciUnit = UnitManager.GetUnit(unit.Name, unit.IsFiller);
                var baseHMM = lookupHMM(ciUnit, HMMPosition.Undefined);
                senoneSequenceList.Add(baseHMM.SenoneSequence);
            }

            // Add this point we have all of the senone sequences that
            // match the base/context pattern collected into the list.
            // Next we build a CompositeSenone consisting of all of the
            // senones in each position of the list.

            // First find the longest senone sequence

            var longestSequence = 0;
            foreach (var ss in senoneSequenceList) 
            {
                if (ss.Senones.Length > longestSequence) 
                {
                    longestSequence = ss.Senones.Length;
                }
            }

            // now collect all of the senones at each position into
            // arrays so we can create CompositeSenones from them
            // QUESTION: is is possible to have different size senone
            // sequences. For now lets assume the worst case.

            var compositeSenones = new List<CompositeSenone>();
            var logWeight = 0.0f;
            for (var i = 0; i < longestSequence; i++) {
                var compositeSenoneSet = new HashSet<ISenone>();
                foreach (var senoneSequence in senoneSequenceList) 
                {
                    if (i < senoneSequence.Senones.Length) 
                    {
                        var senone = senoneSequence.Senones[i];
                        compositeSenoneSet.Add(senone);
                    }
                }
                compositeSenones.Add(CompositeSenone.Create(
                        compositeSenoneSet, logWeight));
            }

            compositeSenoneSequence = SenoneSequence.Create(compositeSenones);
            _compositeSenoneSequenceCache.Put(unit.ToString(), compositeSenoneSequence);

            
            this.LogInfo(unit + " consists of " + compositeSenones.Count + " composite senones");

            compositeSenoneSequence.Dump("am");

            return compositeSenoneSequence;
        }


        /**
        /// Returns the size of the left context for context dependent units
         *
        /// @return the left context size
         */
        public override int GetLeftContextSize() 
        {
            return Loader.LeftContextSize;
        }


        /**
        /// Returns the size of the right context for context dependent units
         *
        /// @return the left context size
         */
        public override int GetRightContextSize() {
            return Loader.RightContextSize;
        }


        /**
        /// Given a unit, returns the HMM that exactly matches the given unit.
         *
        /// @param unit     the unit of interest
        /// @param position the position of the unit of interest
        /// @return the HMM that exactly matches, or null if no match could be found.
         */
        private SenoneHMM lookupHMM(Unit unit, HMMPosition position) {
            return (SenoneHMM) Loader.HMMManager.Get(position, unit);
        }
    
    
        public ISenone GetSenone(long id) 
        {
            return Loader.SenonePool.Get((int)id);
        }

        /** Dumps information about this model to the logger */
        protected void LogInfo() {
            if (Loader != null) {
                Loader.LogInfo();
            }
            this.LogInfo("CompositeSenoneSequences: " +
                    _compositeSenoneSequenceCache.Count);
        }


        /**
        /// Searches an hmm at any position
         *
        /// @param unit the unit to search for
        /// @return hmm the hmm or null if it was not found
         */
        private SenoneHMM GetHMMAtAnyPosition(Unit unit) 
        {
            var mgr = Loader.HMMManager;
            foreach (HMMPosition pos in Enum.GetValues(typeof(HMMPosition))) 
            {
                var hmm = (SenoneHMM)mgr.Get(pos, unit);
                if (hmm != null)
                    return hmm;
            }
            return null;
        }


        /**
        /// Given a unit, search for the HMM associated with this unit by replacing all non-silence filler contexts with the
        /// silence filler context
         *
        /// @param unit the unit of interest
        /// @return the associated hmm or null
         */
        private SenoneHMM GetHMMInSilenceContext(Unit unit, HMMPosition position) 
        {
            SenoneHMM hmm = null;
            var mgr = Loader.HMMManager;
            var context = unit.Context;

            if (context is LeftRightContext) 
            {
                var lrContext = (LeftRightContext) context;

                var lc = lrContext.LeftContext;
                var rc = lrContext.RightContext;

                Unit[] nlc;
                Unit[] nrc;

                if (HasNonSilenceFiller(lc)) {
                    nlc = ReplaceNonSilenceFillerWithSilence(lc);
                } else {
                    nlc = lc;
                }

                if (HasNonSilenceFiller(rc)) {
                    nrc = ReplaceNonSilenceFillerWithSilence(rc);
                } else {
                    nrc = rc;
                }

                if (nlc != lc || nrc != rc) {
                    Context newContext = LeftRightContext.Get(nlc, nrc);
                    var newUnit = UnitManager.GetUnit(unit.Name,
                            unit.IsFiller, newContext);
                    hmm = (SenoneHMM) mgr.Get(position, newUnit);
                    if (hmm == null) {
                        hmm = GetHMMAtAnyPosition(newUnit);
                    }
                }
            }
            return hmm;
        }


        /**
        /// Returns true if the array of units contains a non-silence filler
         *
        /// @param units the units to check
        /// @return true if the array contains a filler that is not the silence filler
         */
        private Boolean HasNonSilenceFiller(Unit[] units) 
        {
            if (units == null) {
                return false;
            }

            foreach (var unit in units) 
            {
                if (unit.IsFiller &&
                    !unit.Equals(UnitManager.Silence)) 
                {
                    return true;
                }
            }
            return false;
        }


        /**
        /// Returns a unit array with all non-silence filler units replaced with the silence filler a non-silence filler
         *
        /// @param context the context to check
        /// @return true if the array contains a filler that is not the silence filler
         */
        private Unit[] ReplaceNonSilenceFillerWithSilence(Unit[] context) 
        {
            var replacementContext = new Unit[context.Length];
            for (var i = 0; i < context.Length; i++) {
                if (context[i].IsFiller &&
                        !context[i].Equals(UnitManager.Silence)) 
                {
                    replacementContext[i] = UnitManager.Silence;
                } else {
                    replacementContext[i] = context[i];
                }
            }
            return replacementContext;
        }


        /**
        /// Returns the properties of this acoustic model.
         *
        /// @return the properties of this acoustic model
         */
        public override JProperties[] GetProperties() 
        {
            //if (properties == null) {
            //    properties = new PropertyInfo[]();
            //    try {
            //        properties.load(getClass().getResource("model.props").openStream());
            //    } 
            //    catch (Exception ioe) 
            //    {
            //        ioe.printStackTrace();
            //    }
            //}
            return properties;
        }
    }
}
